using System.Globalization;
using BufferC.Core.Modbus;
using BufferC.Core.Secs;
using Microsoft.Data.Sqlite;

namespace BufferC.Core.Core;

/// <summary>
/// 表为中心的库存台账（2026-08-16 重构）：三张表——
/// stations 站口主表（每站一行，开机按配置建满；MCS 查询/前端/事件全部从表出）、
/// alarms 告警表（只存当前置起，清除即 DELETE）、device_state 设备状态（单行）。
/// dbPath 为空 = 纯内存；提供 dbPath 时同步持久化 SQLite（WAL，变化才写）。
/// 所有写走 _lock 串行化；取走后保留 carrier_id/install_source（最近一条，再安装覆盖）。
/// </summary>
public sealed class Inventory : IDisposable
{
    /// <summary>站口主表行。state=0~5 站口状态（0空/1有货/2正放/3正取/4故障/5人工有货）；取走保留 carrier_id/source</summary>
    public sealed record StationRow(
        int PlcIndex, int Station, string UnitId,
        ushort State, string CarrierId, string InstallSource, string UpdatedAt,
        ushort Avail, ushort AlarmCode,
        ushort CmdState, ushort CmdType, string CmdCarrierId, string CmdTime, uint CmdSeq, string CmdSource);

    private readonly string? _dbPath;
    private readonly Dictionary<string, StationRow> _stations = new();   // "plc:station" → 行
    private readonly Dictionary<string, string> _byCarrier = new();      // carrierId → key（只在库）
    private readonly List<AlarmStatus> _alarms = new();                  // 告警表内存镜像（清除即删）
    private readonly object _lock = new();
    private readonly object _dbLock = new();                             // 单例连接：所有 SQL 命令串行化（锁序恒为 _lock → _dbLock，不反向）
    private SqliteConnection? _conn;
    private readonly Action<string, string, LogLevel> _log;              // 注入日志（默认 Console 回退，单测 new 不破）
    private int _writeFailStreak;                                        // SQLite 写失败节流计数
    private DateTime _lastFailLogAt = DateTime.MinValue;

    public ushort ControlState { get; private set; } = 5;   // SVID 14：固定 ONLINE REMOTE
    public ushort ScState { get; private set; } = 2;        // SVID 21：固定 PAUSED（MCS 现场约定）

    public Inventory(string? dbPath = null, int plcCount = 1, Action<string, string, LogLevel>? log = null, int historyCap = 2000)
    {
        _dbPath = dbPath;
        _log = log ?? ((c, m, lv) => Console.WriteLine($"[{c}] {m}"));
        _historyCap = historyCap;
        if (dbPath != null) LoadFromDb();   // 先建表并加载已有行
        InitStations(plcCount);             // 建满行（缺的行同步落库，保证后续 UPDATE 有目标）
    }

    /// <summary>按配置建满所有站口空行（重启/新增机台补缺，已有行不动）</summary>
    public void InitStations(int plcCount)
    {
        lock (_lock)
        {
            for (int p = 1; p <= plcCount; p++)
                for (int s = 1; s <= RegisterMap.StationsPerPlc; s++)
                {
                    var key = Key(p, s);
                    if (_stations.ContainsKey(key)) continue;
                    var row = new StationRow(p, s, EventEngine.Unit(p, s), 0, "", "", "", 0, 0, 0, 0, "", "", 0, "");
                    _stations[key] = row;
                    Exec("INSERT OR REPLACE INTO stations(station_key, plc, station, unit_id, state, carrier_id, install_source, avail, alarm_code, updated_at, cmd_state, cmd_type, cmd_carrier_id, cmd_time, cmd_seq, cmd_source) " +
                         "VALUES($k,$p,$s,$u,0,'','',0,0,'',0,0,'','',0,'')",
                         ("$k", key), ("$p", p), ("$s", s), ("$u", row.UnitId));
                }
        }
    }

    // ---------- 投影（SVID / 前端） ----------
    /// <summary>载具清单（SVID 15）：只含有货站口（1/5）</summary>
    public IReadOnlyList<CarrierStatus> Carriers
    {
        get
        {
            lock (_lock)
                return _stations.Values
                    .Where(r => r.State is RegisterMap.StHasCarrier or RegisterMap.StManualCarrier)
                    .Select(r => new CarrierStatus(r.CarrierId, r.UnitId, r.UpdatedAt, 1, r.InstallSource))
                    .ToList();
        }
    }

    /// <summary>单位清单（SVID 29）：全部站口，2=在服/1=停服（由 avail 推导）</summary>
    public IReadOnlyList<UnitStatus> Units
    {
        get
        {
            lock (_lock)
                return _stations.Values
                    .OrderBy(r => r.PlcIndex).ThenBy(r => r.Station)
                    .Select(r => new UnitStatus(r.UnitId, r.Avail == 0 ? (ushort)2 : (ushort)1))
                    .ToList();
        }
    }

    /// <summary>站口主表全量（含已取走历史；前端合并表用）</summary>
    public IReadOnlyList<StationRow> Ledger
    {
        get
        {
            lock (_lock)
                return _stations.Values.OrderBy(r => r.PlcIndex).ThenBy(r => r.Station).ToList();
        }
    }

    public IReadOnlyList<AlarmStatus> Alarms { get { lock (_lock) return _alarms.ToList(); } }

    // ---------- 载具操作 ----------
    /// <summary>安装载具（命令/扫码/对账路径）：state=1、写来源、刷更新时间；同站旧载具索引移除</summary>
    public void SetCarrier(int plcIndex, int station, string carrierId, string loc, string source = "MANUAL")
    {
        var key = Key(plcIndex, station);
        var time = Now();
        lock (_lock)
        {
            if (!_stations.TryGetValue(key, out var row))
                _stations[key] = row = new StationRow(plcIndex, station, EventEngine.Unit(plcIndex, station), 0, "", "", "", 0, 0, 0, 0, "", "", 0, "");
            if (row.CarrierId.Length > 0 && row.CarrierId != carrierId) _byCarrier.Remove(row.CarrierId);
            _stations[key] = row with
            {
                State = RegisterMap.StHasCarrier,
                CarrierId = carrierId,
                InstallSource = source,
                UpdatedAt = time,
            };
            if (carrierId.Length > 0) _byCarrier[carrierId] = key;
            Exec("INSERT OR REPLACE INTO stations(station_key, plc, station, unit_id, state, carrier_id, install_source, avail, alarm_code, updated_at, cmd_state, cmd_type, cmd_carrier_id, cmd_time, cmd_seq, cmd_source) " +
                 "VALUES($k,$p,$s,$u,$st,$c,$src,$a,$al,$t,$cs,$ct,$cc,$ctime,$cseq,$csrc)",
                 ("$k", key), ("$p", plcIndex), ("$s", station), ("$u", row.UnitId),
                 ("$st", (int)RegisterMap.StHasCarrier), ("$c", carrierId), ("$src", source),
                 ("$a", (int)row.Avail), ("$al", (int)row.AlarmCode), ("$t", time),
                 ("$cs", (int)row.CmdState), ("$ct", (int)row.CmdType), ("$cc", row.CmdCarrierId),
                 ("$ctime", row.CmdTime), ("$cseq", (long)row.CmdSeq), ("$csrc", row.CmdSource));
        }
    }

    /// <summary>取走载具：state=0、保留 carrier_id/来源（历史）、刷更新时间；空站口返回 false</summary>
    public bool RemoveCarrier(int plcIndex, int station, out string carrierId)
    {
        carrierId = "";
        var key = Key(plcIndex, station);
        var time = Now();
        lock (_lock)
        {
            if (!_stations.TryGetValue(key, out var row)) return false;
            if (row.State is not (RegisterMap.StHasCarrier or RegisterMap.StManualCarrier)) return false;
            carrierId = row.CarrierId;
            _stations[key] = row with { State = RegisterMap.StEmpty, UpdatedAt = time };
            if (carrierId.Length > 0) _byCarrier.Remove(carrierId);
            Exec("UPDATE stations SET state=0, updated_at=$t WHERE station_key=$k", ("$t", time), ("$k", key));
            return true;
        }
    }

    /// <summary>
    /// 轮询同步（500ms）：状态/告警/可用逐列对比，无变化零 SQL。
    /// 有货(1/5)且表无 ID 时用 PLC 实况兜底（重启/断线重连场景）；
    /// 快照读到 0 但表里有载具时不覆盖（取走由事件路径处理，防命令/PLC 时序竞态——与历史行为一致）。
    /// </summary>
    public void UpdateStationState(int plcIndex, int station, ushort state, ushort alarm, ushort avail, string? snapshotCarrierId)
    {
        var key = Key(plcIndex, station);
        lock (_lock)
        {
            if (!_stations.TryGetValue(key, out var row)) return;
            if (state == RegisterMap.StEmpty
                && (row.State is RegisterMap.StHasCarrier or RegisterMap.StManualCarrier)
                && row.CarrierId.Length > 0)
                state = row.State;   // 竞态防护：取走交给事件路径
            var carrierId = row.CarrierId;
            if (carrierId.Length == 0
                && (state is RegisterMap.StHasCarrier or RegisterMap.StManualCarrier)
                && !string.IsNullOrEmpty(snapshotCarrierId))
                carrierId = snapshotCarrierId;
            if (row.State == state && row.AlarmCode == alarm && row.Avail == avail && row.CarrierId == carrierId) return;
            _stations[key] = row with
            {
                State = state,
                AlarmCode = alarm,
                Avail = avail,
                CarrierId = carrierId,
                UpdatedAt = Now(),
            };
            Exec("UPDATE stations SET state=$st, alarm_code=$al, avail=$a, carrier_id=$c, updated_at=$t WHERE station_key=$k",
                 ("$st", (int)state), ("$al", (int)alarm), ("$a", (int)avail), ("$c", carrierId), ("$t", _stations[key].UpdatedAt), ("$k", key));
        }
    }

    /// <summary>单位在服/停服（301/302 事件路径）：regAvail 0=在服/1=停服，变化才写</summary>
    public void UpdateUnit(int plcIndex, int station, ushort regAvail)
    {
        var key = Key(plcIndex, station);
        lock (_lock)
        {
            if (!_stations.TryGetValue(key, out var row) || row.Avail == regAvail) return;
            _stations[key] = row with { Avail = regAvail, UpdatedAt = Now() };
            Exec("UPDATE stations SET avail=$a, updated_at=$t WHERE station_key=$k",
                 ("$a", (int)regAvail), ("$t", _stations[key].UpdatedAt), ("$k", key));
        }
    }

    public bool FindCarrier(string carrierId, out int plcIndex, out int station)
    {
        plcIndex = 0; station = 0;
        lock (_lock)
        {
            if (!_byCarrier.TryGetValue(carrierId, out var key)) return false;
            var parts = key.Split(':');
            plcIndex = int.Parse(parts[0]);
            station = int.Parse(parts[1]);
            return true;
        }
    }

    // ---------- 告警表（只存当前置起，清除即 DELETE） ----------
    public bool AlarmSet(uint alarmId, string unitId, string text)
    {
        lock (_lock)
        {
            if (_alarms.Any(a => a.AlarmId == alarmId)) return false;
            _alarms.Add(new AlarmStatus(unitId, alarmId, text));
            Exec("INSERT OR REPLACE INTO alarms(alarm_id, unit_id, text) VALUES($a,$u,$t)",
                 ("$a", (long)alarmId), ("$u", unitId), ("$t", text));
            return true;
        }
    }

    public bool AlarmClear(uint alarmId)
    {
        lock (_lock)
        {
            var n = _alarms.RemoveAll(a => a.AlarmId == alarmId);
            if (n > 0) Exec("DELETE FROM alarms WHERE alarm_id=$a", ("$a", (long)alarmId));
            return n > 0;
        }
    }

    public bool MarkAlarmAcked(uint alarmId)
    {
        lock (_lock)
        {
            int idx = _alarms.FindIndex(a => a.AlarmId == alarmId);
            if (idx < 0) return false;
            _alarms[idx] = _alarms[idx] with { Acked = true };
            return true;
        }
    }

    // ---------- 命令队列（第二期异步命令：先写表回 ACK，后台执行，重启恢复） ----------
    /// <summary>命令意向入表：cmd_state=1 待执行 + 命令元数据（类型/载具/时间/来源）</summary>
    public void QueueCommand(int plcIndex, int station, ushort cmdType, string carrierId, string source)
    {
        var key = Key(plcIndex, station);
        var time = Now();
        lock (_lock)
        {
            if (!_stations.TryGetValue(key, out var row)) return;
            _stations[key] = row with { CmdState = 1, CmdType = cmdType, CmdCarrierId = carrierId, CmdTime = time, CmdSource = source };
            Exec("UPDATE stations SET cmd_state=1, cmd_type=$ct, cmd_carrier_id=$cc, cmd_time=$t, cmd_source=$cs WHERE station_key=$k",
                 ("$ct", (int)cmdType), ("$cc", carrierId), ("$t", time), ("$cs", source), ("$k", key));
        }
    }

    /// <summary>待执行命令行（后台工作线程扫描；断线保持待执行；4=恢复待校验同样入队，worker 校验通过后转 1 执行）</summary>
    public IReadOnlyList<StationRow> PendingCommands
    {
        get { lock (_lock) return _stations.Values.Where(r => r.CmdState is 1 or 4).ToList(); }
    }

    /// <summary>命令开始执行：cmd_state=2（编号在 CommandChannel 内部才分配，此处先写 0；执行结束后成功经 CompleteCommand、失败经 WriteCommandSeq 回写）</summary>
    public void MarkCommandRunning(int plcIndex, int station, uint seq)
    {
        var key = Key(plcIndex, station);
        lock (_lock)
        {
            if (!_stations.TryGetValue(key, out var row)) return;
            _stations[key] = row with { CmdState = 2, CmdSeq = seq };
            Exec("UPDATE stations SET cmd_state=2, cmd_seq=$q WHERE station_key=$k", ("$q", (long)seq), ("$k", key));
        }
    }

    /// <summary>命令成功：cmd_state=0 + 记录命令编号（保留类型/载具/时间/来源作最近一条历史）</summary>
    public void CompleteCommand(int plcIndex, int station, uint seq)
    {
        var key = Key(plcIndex, station);
        lock (_lock)
        {
            if (!_stations.TryGetValue(key, out var row)) return;
            _stations[key] = row with { CmdState = 0, CmdSeq = seq };
            Exec("UPDATE stations SET cmd_state=0, cmd_seq=$q WHERE station_key=$k", ("$q", (long)seq), ("$k", key));
        }
    }

    /// <summary>命令失败：cmd_state=3</summary>
    public void FailCommand(int plcIndex, int station)
    {
        var key = Key(plcIndex, station);
        lock (_lock)
        {
            if (!_stations.TryGetValue(key, out var row)) return;
            _stations[key] = row with { CmdState = 3 };
            Exec("UPDATE stations SET cmd_state=3 WHERE station_key=$k", ("$k", key));
        }
    }

    /// <summary>回写命令编号（执行结束时调用：失败命令也要记录最后一次 400 触发值；MarkCommandRunning 时编号尚未分配故先写 0）</summary>
    public void WriteCommandSeq(int plcIndex, int station, uint seq)
    {
        var key = Key(plcIndex, station);
        lock (_lock)
        {
            if (!_stations.TryGetValue(key, out var row)) return;
            _stations[key] = row with { CmdSeq = seq };
            Exec("UPDATE stations SET cmd_seq=$q WHERE station_key=$k", ("$q", (long)seq), ("$k", key));
        }
    }

    /// <summary>重启恢复（B4 策略 2026-08-17 用户确认：全部重执行）：未完成命令（待执行/执行中）→ 4=恢复待校验
    /// （worker 校验站口当前载具 ID 与命令一致后转 1 执行；不一致标记失败人工介入），返回列表供日志。</summary>
    public IReadOnlyList<StationRow> RequeueStaleCommands()
    {
        lock (_lock)
        {
            var stale = _stations.Values.Where(r => r.CmdState is 1 or 2).ToList();
            foreach (var row in stale)
            {
                var key = Key(row.PlcIndex, row.Station);
                _stations[key] = row with { CmdState = 4 };
                Exec("UPDATE stations SET cmd_state=4 WHERE station_key=$k", ("$k", key));
            }
            return stale;
        }
    }

    /// <summary>恢复校验通过：4 → 1（交给正常执行路径）</summary>
    public void RequeueCommand(int plcIndex, int station)
    {
        var key = Key(plcIndex, station);
        lock (_lock)
        {
            if (!_stations.TryGetValue(key, out var row)) return;
            _stations[key] = row with { CmdState = 1 };
            Exec("UPDATE stations SET cmd_state=1 WHERE station_key=$k", ("$k", key));
        }
    }

    // ---------- 历史持久化（B3：事件/命令/告警/悬空命令重启不丢；每表只保留最近 _historyCap 条） ----------
    private readonly int _historyCap;

    public sealed record HistoryEvent(DateTime Time, ushort Ceid, string Description);
    public sealed record HistoryCommand(DateTime Time, string Source, int Plc, int Station, string Cmd, bool Ok, long ElapsedMs, string CarrierId);
    public sealed record HistoryAlarm(DateTime Time, string UnitId, uint AlarmId, string Text, string Action);
    public sealed record HistoryFailed(DateTime Time, string Source, string Cmd, string CarrierId, string Loc);

    public void AddEventHistory(DateTime t, ushort ceid, string desc) =>
        Exec("INSERT INTO history_events(time, ceid, description) VALUES($t,$c,$d); " +
             $"DELETE FROM history_events WHERE id <= (SELECT MAX(id) FROM history_events) - {_historyCap}",
             ("$t", NowOf(t)), ("$c", (int)ceid), ("$d", desc));

    public void AddCommandHistory(DateTime t, string source, int plc, int station, string cmd, bool ok, long elapsedMs, string carrierId) =>
        Exec("INSERT INTO history_commands(time, source, plc, station, cmd, ok, elapsed_ms, carrier_id) VALUES($t,$s,$p,$st,$c,$o,$e,$id); " +
             $"DELETE FROM history_commands WHERE id <= (SELECT MAX(id) FROM history_commands) - {_historyCap}",
             ("$t", NowOf(t)), ("$s", source), ("$p", plc), ("$st", station), ("$c", cmd), ("$o", ok ? 1 : 0), ("$e", elapsedMs), ("$id", carrierId));

    public void AddAlarmHistory(DateTime t, string unitId, uint alarmId, string text, string action) =>
        Exec("INSERT INTO history_alarms(time, unit_id, alarm_id, text, action) VALUES($t,$u,$a,$x,$act); " +
             $"DELETE FROM history_alarms WHERE id <= (SELECT MAX(id) FROM history_alarms) - {_historyCap}",
             ("$t", NowOf(t)), ("$u", unitId), ("$a", (long)alarmId), ("$x", text), ("$act", action));

    public void AddFailedHistory(DateTime t, string source, string cmd, string carrierId, string loc) =>
        Exec("INSERT INTO history_failed(time, source, cmd, carrier_id, loc) VALUES($t,$s,$c,$id,$l); " +
             $"DELETE FROM history_failed WHERE id <= (SELECT MAX(id) FROM history_failed) - {_historyCap}",
             ("$t", NowOf(t)), ("$s", source), ("$c", cmd), ("$id", carrierId), ("$l", loc));

    private static string NowOf(DateTime t) => t.ToString("yyyyMMddHHmmssfff");
    private static DateTime ParseTime(string s) =>
        DateTime.TryParseExact(s, "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : DateTime.MinValue;

    public List<HistoryEvent> LoadEvents(int tail)
    {
        var rows = new List<HistoryEvent>();
        if (_dbPath == null) return rows;
        try
        {
            lock (_dbLock)
            {
                using var cmd = Conn().CreateCommand();
                cmd.CommandText = "SELECT time, ceid, description FROM history_events ORDER BY id DESC LIMIT $n";
                cmd.Parameters.AddWithValue("$n", tail);
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.Add(new HistoryEvent(ParseTime(DStr(r, 0)), (ushort)r.GetInt64(1), DStr(r, 2)));
                rows.Reverse();
            }
        }
        catch (Exception e) { _log("Inventory", $"历史事件加载失败: {e.Message}", LogLevel.Warn); }
        return rows;
    }

    public List<HistoryCommand> LoadCommands(int tail)
    {
        var rows = new List<HistoryCommand>();
        if (_dbPath == null) return rows;
        try
        {
            lock (_dbLock)
            {
                using var cmd = Conn().CreateCommand();
                cmd.CommandText = "SELECT time, source, plc, station, cmd, ok, elapsed_ms, carrier_id FROM history_commands ORDER BY id DESC LIMIT $n";
                cmd.Parameters.AddWithValue("$n", tail);
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.Add(new HistoryCommand(ParseTime(DStr(r, 0)), DStr(r, 1), r.GetInt32(2), r.GetInt32(3),
                    DStr(r, 4), r.GetInt64(5) == 1, r.GetInt64(6), DStr(r, 7)));
                rows.Reverse();
            }
        }
        catch (Exception e) { _log("Inventory", $"历史命令加载失败: {e.Message}", LogLevel.Warn); }
        return rows;
    }

    public List<HistoryAlarm> LoadAlarms(int tail)
    {
        var rows = new List<HistoryAlarm>();
        if (_dbPath == null) return rows;
        try
        {
            lock (_dbLock)
            {
                using var cmd = Conn().CreateCommand();
                cmd.CommandText = "SELECT time, unit_id, alarm_id, text, action FROM history_alarms ORDER BY id DESC LIMIT $n";
                cmd.Parameters.AddWithValue("$n", tail);
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.Add(new HistoryAlarm(ParseTime(DStr(r, 0)), DStr(r, 1), (uint)r.GetInt64(2), DStr(r, 3), DStr(r, 4)));
                rows.Reverse();
            }
        }
        catch (Exception e) { _log("Inventory", $"历史告警加载失败: {e.Message}", LogLevel.Warn); }
        return rows;
    }

    public List<HistoryFailed> LoadFailed(int tail)
    {
        var rows = new List<HistoryFailed>();
        if (_dbPath == null) return rows;
        try
        {
            lock (_dbLock)
            {
                using var cmd = Conn().CreateCommand();
                cmd.CommandText = "SELECT time, source, cmd, carrier_id, loc FROM history_failed ORDER BY id DESC LIMIT $n";
                cmd.Parameters.AddWithValue("$n", tail);
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.Add(new HistoryFailed(ParseTime(DStr(r, 0)), DStr(r, 1), DStr(r, 2), DStr(r, 3), DStr(r, 4)));
                rows.Reverse();
            }
        }
        catch (Exception e) { _log("Inventory", $"悬空命令历史加载失败: {e.Message}", LogLevel.Warn); }
        return rows;
    }

    // ---------- 设备状态（SVID 14/21） ----------
    public void SetDeviceState(ushort controlState, ushort scState)
    {
        lock (_lock)
        {
            if (ControlState == controlState && ScState == scState) return;
            ControlState = controlState;
            ScState = scState;
            Exec("INSERT OR REPLACE INTO device_state(id, control_state, sc_state) VALUES(1,$c,$s)",
                 ("$c", (int)controlState), ("$s", (int)scState));
        }
    }

    private static string Key(int plcIndex, int station) => $"{plcIndex}:{station}";
    private static string Now() => DateTime.Now.ToString("yyyyMMddHHmmssfff");

    // ---------- SQLite 持久化（WAL；旧版 carriers/units 表不再读取） ----------
    private void LoadFromDb()
    {
        try
        {
            lock (_dbLock)
            {
                var conn = Conn();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = """
                        CREATE TABLE IF NOT EXISTS stations(
                            station_key TEXT PRIMARY KEY, plc INTEGER, station INTEGER, unit_id TEXT,
                            state INTEGER, carrier_id TEXT, install_source TEXT DEFAULT '',
                            avail INTEGER, alarm_code INTEGER, updated_at TEXT DEFAULT '',
                            cmd_state INTEGER DEFAULT 0, cmd_type INTEGER DEFAULT 0, cmd_carrier_id TEXT DEFAULT '',
                            cmd_time TEXT DEFAULT '', cmd_seq INTEGER DEFAULT 0, cmd_source TEXT DEFAULT '');
                        CREATE TABLE IF NOT EXISTS alarms(alarm_id INTEGER PRIMARY KEY, unit_id TEXT, text TEXT);
                        CREATE TABLE IF NOT EXISTS device_state(id INTEGER PRIMARY KEY, control_state INTEGER, sc_state INTEGER);
                        CREATE TABLE IF NOT EXISTS history_events(id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT, ceid INTEGER, description TEXT);
                        CREATE TABLE IF NOT EXISTS history_commands(id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT, source TEXT, plc INTEGER, station INTEGER, cmd TEXT, ok INTEGER, elapsed_ms INTEGER, carrier_id TEXT);
                        CREATE TABLE IF NOT EXISTS history_alarms(id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT, unit_id TEXT, alarm_id INTEGER, text TEXT, action TEXT);
                        CREATE TABLE IF NOT EXISTS history_failed(id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT, source TEXT, cmd TEXT, carrier_id TEXT, loc TEXT);
                        """;
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT station_key, plc, station, unit_id, state, carrier_id, install_source, avail, alarm_code, updated_at, cmd_state, cmd_type, cmd_carrier_id, cmd_time, cmd_seq, cmd_source FROM stations";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var key = r.GetString(0);
                        var row = new StationRow(r.GetInt32(1), r.GetInt32(2), r.GetString(3),
                            (ushort)r.GetInt32(4), DStr(r, 5), DStr(r, 6), DStr(r, 9),
                            (ushort)r.GetInt32(7), (ushort)r.GetInt32(8),
                            (ushort)r.GetInt32(10), (ushort)r.GetInt32(11), DStr(r, 12), DStr(r, 13), (uint)r.GetInt64(14), DStr(r, 15));
                        _stations[key] = row;
                        if ((row.State is RegisterMap.StHasCarrier or RegisterMap.StManualCarrier) && row.CarrierId.Length > 0)
                            _byCarrier[row.CarrierId] = key;
                    }
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT alarm_id, unit_id, text FROM alarms";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) _alarms.Add(new AlarmStatus(DStr(r, 1), (uint)r.GetInt64(0), DStr(r, 2)));
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT control_state, sc_state FROM device_state WHERE id=1";
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                    {
                        ControlState = (ushort)r.GetInt32(0);
                        ScState = (ushort)r.GetInt32(1);
                    }
                }
            }
        }
        catch (Exception e)
        {
            _log("Inventory", $"SQLite 加载失败（降级为内存模式）: {e.Message}", LogLevel.Warn);
        }
    }

    private static string DStr(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);

    private void Exec(string sql, params (string Name, object Value)[] pars)
    {
        if (_dbPath == null) return;
        try
        {
            lock (_dbLock)
            {
                using var cmd = Conn().CreateCommand();
                cmd.CommandText = sql;
                foreach (var (name, value) in pars) cmd.Parameters.AddWithValue(name, value);
                cmd.ExecuteNonQuery();
                if (_writeFailStreak > 0)   // 失败后恢复：Info 收尾并清零
                {
                    int n = _writeFailStreak;
                    _writeFailStreak = 0;
                    _log("Inventory", $"SQLite 写入恢复（此前连续失败 {n} 次）", LogLevel.Info);
                }
            }
        }
        catch (Exception e)
        {
            // 节流：首次 ERROR，之后每 10 次或每 60s 一条 Debug（带累计数），避免高频写路径失败刷屏
            _writeFailStreak++;
            if (_writeFailStreak == 1 || _writeFailStreak % 10 == 0 || (DateTime.Now - _lastFailLogAt).TotalSeconds >= 60)
            {
                _log("Inventory", $"SQLite 写入失败: {e.Message}（连续第 {_writeFailStreak} 次）",
                    _writeFailStreak == 1 ? LogLevel.Error : LogLevel.Debug);
                _lastFailLogAt = DateTime.Now;
            }
        }
    }

    /// <summary>单例连接（D1：不再每写开一次连接）：懒打开 + WAL 只设一次；所有命令在 _dbLock 内使用</summary>
    private SqliteConnection Conn()
    {
        lock (_dbLock)
        {
            if (_conn == null)
            {
                _conn = new SqliteConnection($"Data Source={_dbPath}");
                _conn.Open();
                using var pragma = _conn.CreateCommand();
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                pragma.ExecuteNonQuery();
            }
            return _conn;
        }
    }

    public void Dispose()
    {
        lock (_dbLock)
        {
            _conn?.Dispose();
            _conn = null;
        }
    }
}
