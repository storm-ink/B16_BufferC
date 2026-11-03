using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BufferC.Core.Config;
using BufferC.Core.Modbus;

namespace BufferC.Core.Core;

/// <summary>AGV 货格加解锁（V9）：入站信号 → 校验/去重 → 命令通道下发 3/4 → 回读 341 确认 → 镜像落表。
/// PLC 341~356 是唯一权威；本侧状态只是镜像，转发失败绝不记锁。</summary>
public sealed class CellLockManager
{
    public sealed record CellLockRequest(string Guid, string CmsIndex, string Action, DateTime AgentTime);
    public sealed record CellLockView(int Plc, int Station, string CmsIndex, bool Locked, string Source,
        string Guid, string LockedAt, string AgentTime, string UpdatedAt);
    public sealed record CellLockEventView(string Time, string Guid, int Plc, int Station, string Action,
        string Result, string Message, uint CmdSeq, string AgentTime);
    public sealed record CellLockResult(string Code, string Message);

    private const string Category = "锁";
    private const int ReconcileGuardMs = 3000;   // 状态落库后 3s 内不对账（≥2×轮询周期，防陈旧快照反向覆盖）
    private const int TimeoutLoopMs = 5000;
    private const int GuidCacheCap = 2000;

    private static readonly string[] TimeFormats =
    {
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss", "yyyyMMddHHmmss",
    };

    private readonly BufferCConfig _cfg;
    private readonly Inventory _inv;
    private readonly Func<int, PlcPoller?> _findPoller;
    private readonly Func<string, (bool Ok, int Plc, int Station)> _parseCmsIndex;
    private readonly Func<DateTime> _clock;
    private readonly Action<string, string, LogLevel> _log;
    private readonly Action<string, string> _audit;
    private readonly Action<DateTime, string, int, int, string, bool, long> _onCommand;

    private readonly ConcurrentDictionary<(int Plc, int Station), SemaphoreSlim> _gates = new();
    private readonly object _stateLock = new();                       // 对账 + 本侧状态写入原子化
    private readonly Dictionary<string, long> _lastChange = new();    // "plc:station" → TickCount64
    private readonly Dictionary<string, long> _timeoutFailLogged = new();
    private readonly Dictionary<string, Inventory.CellLockEventRow> _guidCache = new(StringComparer.Ordinal);
    private readonly RingBuffer<Inventory.CellLockEventRow> _recent = new(100);

    public CellLockManager(BufferCConfig cfg, Inventory inv,
        Func<int, PlcPoller?> findPoller,
        Func<string, (bool Ok, int Plc, int Station)> parseCmsIndex,
        Func<DateTime> clock,
        Action<string, string, LogLevel> log,
        Action<string, string> audit,
        Action<DateTime, string, int, int, string, bool, long> onCommand)
    {
        _cfg = cfg;
        _inv = inv;
        _findPoller = findPoller;
        _parseCmsIndex = parseCmsIndex;
        _clock = clock;
        _log = log;
        _audit = audit;
        _onCommand = onCommand;

        // GUID 去重缓存：启动从 DB 回填最近流水；不满意的历史（FAIL）允许同 GUID 重试
        foreach (var e in _inv.LoadCellLockEvents(GuidCacheCap))
        {
            _recent.Add(e);
            if (e.Result != "FAIL") _guidCache[e.Guid] = e;
        }
    }

    // ---------- 入站处理 ----------
    public CellLockResult Process(string body)
    {
        try
        {
            if (!TryParse(body, out var req, out var err)) return new CellLockResult("1", err);
            return Process(req);
        }
        catch (Exception e)
        {
            _log(Category, $"加解锁请求处理异常: {e.Message}", LogLevel.Error);
            return new CellLockResult("1", "PLC 转发失败");
        }
    }

    private CellLockResult Process(CellLockRequest req)
    {
        var (cmsOk, plc, station) = _parseCmsIndex(req.CmsIndex);
        if (!cmsOk || plc < 1)
            return new CellLockResult("1", "参数错误：cmsIndex 非法");
        var poller = _findPoller(plc);
        if (poller == null || station < 1 || station > poller.Config.Stations)
            return new CellLockResult("1", "参数错误：cmsIndex 非法");
        if (!poller.IsConnected)
            return new CellLockResult("1", "PLC 离线");

        bool lockIt = req.Action == "lock";
        string actionTag = lockIt ? "加锁" : "解锁";

        // GUID 去重（成功/幂等的同内容消息直接返回；失败允许重试）
        var prev = FindGuid(req.Guid);
        if (prev != null && prev.Result != "FAIL")
        {
            if (prev.Action == req.Action && prev.PlcIndex == plc && prev.Station == station)
            {
                _log(Category, $"重复消息（幂等）: guid={req.Guid} PLC{plc} 站口{station} {req.Action}", LogLevel.Debug);
                return new CellLockResult("0", "重复消息（幂等）");
            }
            _log(Category, $"GUID 复用但内容不同（按新消息处理）: guid={req.Guid} 原={prev.Action}/PLC{prev.PlcIndex}站口{prev.Station} 现={req.Action}/PLC{plc}站口{station}", LogLevel.Warn);
        }

        var gate = GateOf(plc, station);
        gate.Wait();
        try
        {
            MarkLocalChange(plc, station);   // 命令窗口内 poller 快照不可信（可能在命令前读取），对账先让路
            var now = _clock();
            var cur = ReadLocked(poller, station);
            if (cur == null)
            {
                Record(req, plc, station, "FAIL", "锁状态读取失败", 0);
                _log(Category, $"{actionTag}失败（341 读取失败）: PLC{plc} 站口{station} guid={req.Guid}", LogLevel.Warn);
                return new CellLockResult("1", "PLC 转发失败");
            }
            if (cur.Value == lockIt)
            {
                // 幂等：PLC 已是目标态。加锁且持有者不同（新任务再锁同一格）→ 刷新计时，避免旧时间戳触发超时解锁
                if (lockIt)
                {
                    var row = _inv.FindCellLock(plc, station);
                    if (row == null || !row.Locked || row.Guid != req.Guid)
                        SetLockState(plc, station, "AGV", req.Guid, now, req.AgentTime);
                }
                Record(req, plc, station, "IDEM", lockIt ? "已锁，未下发" : "未锁，未下发", 0);
                _log(Category, $"{actionTag}幂等（341 已是目标态，未下发 PLC）: PLC{plc} 站口{station} guid={req.Guid}", LogLevel.Info);
                return new CellLockResult("0", lockIt ? "加锁成功" : "解锁成功");
            }

            uint seq = 0;
            var sw = Stopwatch.StartNew();
            bool ok;
            try
            {
                ok = poller.Channel.Execute(station, lockIt ? RegisterMap.CmdLock : RegisterMap.CmdUnlock, null, out seq);
            }
            catch (Exception e)
            {
                _log(Category, $"{actionTag}命令异常: PLC{plc} 站口{station} guid={req.Guid}: {e.Message}", LogLevel.Error);
                ok = false;
            }
            sw.Stop();
            _onCommand?.Invoke(_clock(), "AGV", plc, station, lockIt ? "CellLock" : "CellUnlock", ok, sw.ElapsedMilliseconds);

            if (!ok)
            {
                Record(req, plc, station, "FAIL", "PLC 转发失败", seq);
                _log(Category, $"{actionTag}失败（命令通道转发失败）: PLC{plc} 站口{station} guid={req.Guid} seq={seq}", LogLevel.Warn);
                _audit(Category, $"{actionTag}失败: PLC{plc} 站口{station} guid={req.Guid} seq={seq}");
                return new CellLockResult("1", "PLC 转发失败");
            }

            var after = ReadLocked(poller, station);
            if (after != lockIt)
            {
                Record(req, plc, station, "FAIL", "回读不一致", seq);
                _log(Category, $"{actionTag}回读不一致（341 未按预期变化，PLC 可能未实现 V9）: PLC{plc} 站口{station} guid={req.Guid} seq={seq}", LogLevel.Error);
                _audit(Category, $"{actionTag}回读不一致: PLC{plc} 站口{station} guid={req.Guid} seq={seq}");
                return new CellLockResult("1", "PLC 转发失败");
            }

            if (lockIt) SetLockState(plc, station, "AGV", req.Guid, now, req.AgentTime);
            else ClearLockState(plc, station, "AGV", req.Guid, req.AgentTime);
            Record(req, plc, station, "OK", lockIt ? "加锁成功" : "解锁成功", seq);
            _log(Category, $"{actionTag}成功: PLC{plc} 站口{station} guid={req.Guid} seq={seq}（{sw.ElapsedMilliseconds}ms）", LogLevel.Info);
            _audit(Category, $"{actionTag}成功: PLC{plc} 站口{station} guid={req.Guid} seq={seq}");
            return new CellLockResult("0", lockIt ? "加锁成功" : "解锁成功");
        }
        finally
        {
            gate.Release();
        }
    }

    // ---------- 漂移对账（每轮轮询调用） ----------
    /// <summary>PLC 与本地镜像不一致时以 PLC 为准；341 读取失败（V8/异常）时整轮跳过，防把本侧锁误清。</summary>
    public void OnSnapshot(PlcSnapshot snap)
    {
        if (!snap.StationLockValid) return;
        for (int l = 1; l <= snap.StationCount; l++)
        {
            bool plcLocked = snap.StationLock[l - 1] == 1;
            lock (_stateLock)
            {
                var key = Key(snap.PlcIndex, l);
                if (_lastChange.TryGetValue(key, out long t) && Environment.TickCount64 - t < ReconcileGuardMs) continue;
                var row = _inv.FindCellLock(snap.PlcIndex, l);
                bool local = row?.Locked ?? false;
                if (plcLocked == local) continue;
                var now = _clock();
                _lastChange[key] = Environment.TickCount64;
                if (plcLocked)
                {
                    _inv.SetCellLock(snap.PlcIndex, l, "PLC", "", now, DateTime.MinValue);
                    _log(Category, $"锁状态对齐（采纳 PLC）: PLC{snap.PlcIndex} 站口{l} PLC 已锁、本侧无记录", LogLevel.Warn);
                    _audit(Category, $"锁状态对齐（采纳 PLC）: PLC{snap.PlcIndex} 站口{l}");
                }
                else
                {
                    _inv.ClearCellLock(snap.PlcIndex, l, "PLC", row?.Guid ?? "", DateTime.MinValue);
                    _log(Category, $"锁状态对齐（清除本侧）: PLC{snap.PlcIndex} 站口{l} PLC 已解锁（可能上电清零）", LogLevel.Warn);
                    _audit(Category, $"锁状态对齐（清除本侧）: PLC{snap.PlcIndex} 站口{l}");
                }
            }
        }
    }

    // ---------- 超时兜底 ----------
    public async Task TimeoutLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { CheckTimeouts(); }
            catch (Exception e) { _log(Category, $"锁超时扫描异常: {e.Message}", LogLevel.Error); }
            try { await Task.Delay(TimeoutLoopMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>超时扫描（public：测试可拨 Clock 后确定性调用）——加锁超时未解锁 → 补发命令 4；仅 WARN + 审计，不发告警。</summary>
    public void CheckTimeouts()
    {
        int minutes = _cfg.CellLockTimeoutMinutes;
        if (minutes <= 0) return;
        var now = _clock();
        foreach (var row in _inv.CellLocks.Where(r => r.Locked))
        {
            if (row.LockedAt == DateTime.MinValue || (now - row.LockedAt).TotalMinutes < minutes) continue;
            var poller = _findPoller(row.PlcIndex);
            if (poller == null || !poller.IsConnected) continue;
            var gate = GateOf(row.PlcIndex, row.Station);
            gate.Wait();
            try
            {
                MarkLocalChange(row.PlcIndex, row.Station);
                // 取锁后重新确认：期间 AGV 可能已解锁/重新加锁（新 GUID / 新起点）
                var cur = _inv.FindCellLock(row.PlcIndex, row.Station);
                if (cur == null || !cur.Locked || cur.LockedAt != row.LockedAt || cur.Guid != row.Guid) continue;
                if (ReadLocked(poller, row.Station) != true) continue;

                uint seq = 0;
                var sw = Stopwatch.StartNew();
                bool ok;
                try { ok = poller.Channel.Execute(row.Station, RegisterMap.CmdUnlock, null, out seq); }
                catch (Exception e)
                {
                    _log(Category, $"超时解锁命令异常: PLC{row.PlcIndex} 站口{row.Station}: {e.Message}", LogLevel.Error);
                    ok = false;
                }
                sw.Stop();
                _onCommand?.Invoke(_clock(), "TIMEOUT", row.PlcIndex, row.Station, "CellUnlock", ok, sw.ElapsedMilliseconds);

                if (ok && ReadLocked(poller, row.Station) == false)
                {
                    ClearLockState(row.PlcIndex, row.Station, "TIMEOUT", row.Guid, DateTime.MinValue);
                    Record(new CellLockRequest(row.Guid, "", "unlock", DateTime.MinValue),
                        row.PlcIndex, row.Station, "TIMEOUT", "锁超时自动解锁", seq);
                    _log(Category, $"货格锁超时自动解锁: PLC{row.PlcIndex} 站口{row.Station}（锁定 {minutes} 分钟，guid={row.Guid}）", LogLevel.Warn);
                    _audit(Category, $"货格锁超时自动解锁: PLC{row.PlcIndex} 站口{row.Station} guid={row.Guid} seq={seq}");
                    continue;
                }

                var key = Key(row.PlcIndex, row.Station);
                bool logIt;
                lock (_stateLock)
                {
                    logIt = !_timeoutFailLogged.TryGetValue(key, out long last) || Environment.TickCount64 - last >= 60_000;
                    if (logIt) _timeoutFailLogged[key] = Environment.TickCount64;
                }
                if (logIt)
                {
                    _log(Category, $"货格锁超时解锁失败（保持锁，下轮重试）: PLC{row.PlcIndex} 站口{row.Station} guid={row.Guid} seq={seq}", LogLevel.Warn);
                    _audit(Category, $"货格锁超时解锁失败: PLC{row.PlcIndex} 站口{row.Station} guid={row.Guid} seq={seq}");
                }
            }
            finally { gate.Release(); }
        }
    }

    // ---------- 查询（GET /api/agv/locks） ----------
    public (IReadOnlyList<CellLockView> Locks, IReadOnlyList<CellLockEventView> Recent) GetLocks()
    {
        var list = new List<CellLockView>();
        int b = _cfg.Agvc.CmsIndexBase;
        foreach (var p in _cfg.Plcs.OrderBy(x => x.Index))
        {
            int cnt = Math.Min(RegisterMap.StationsPerPlc, p.Stations);
            for (int s = 1; s <= cnt; s++)
            {
                var row = _inv.FindCellLock(p.Index, s);
                list.Add(new CellLockView(p.Index, s, (p.Index * b + s).ToString(),
                    row?.Locked ?? false, row?.Source ?? "", row?.Guid ?? "",
                    FmtTime(row?.LockedAt), FmtTime(row?.AgentTime), FmtTime(row?.UpdatedAt)));
            }
        }
        var recent = _recent.GetTail(100).Select(e => new CellLockEventView(
            FmtTime(e.Time), e.Guid, e.PlcIndex, e.Station, e.Action, e.Result, e.Message, e.CmdSeq, FmtTime(e.AgentTime))).ToList();
        return (list, recent);
    }

    // ---------- 内部 ----------
    private SemaphoreSlim GateOf(int plc, int station) =>
        _gates.GetOrAdd((plc, station), _ => new SemaphoreSlim(1, 1));

    private static string Key(int plc, int station) => $"{plc}:{station}";

    /// <summary>直读 341+物理-1（2 次尝试）；读失败返回 null（调用方按转发失败处理，不改状态）</summary>
    private bool? ReadLocked(PlcPoller poller, int station)
    {
        int phys;
        try { phys = StationMap.PhysicalOf(station, poller.Config.Stations); }
        catch (ArgumentOutOfRangeException) { return null; }
        for (int i = 0; i < 2; i++)
        {
            try { return poller.ReadRegs(RegisterMap.RegStationLock + phys - 1, 1)[0] == 1; }
            catch (Exception e)
            {
                if (i == 1) _log(Category, $"读取 341 锁状态失败: PLC{poller.Config.Index} 站口{station}: {e.Message}", LogLevel.Debug);
                else Thread.Sleep(50);
            }
        }
        return null;
    }

    private void MarkLocalChange(int plc, int station)
    {
        lock (_stateLock) _lastChange[Key(plc, station)] = Environment.TickCount64;
    }

    private void SetLockState(int plc, int station, string source, string guid, DateTime lockedAt, DateTime agentTime)
    {
        lock (_stateLock)
        {
            _lastChange[Key(plc, station)] = Environment.TickCount64;
            _inv.SetCellLock(plc, station, source, guid, lockedAt, agentTime);
        }
    }

    private void ClearLockState(int plc, int station, string source, string guid, DateTime agentTime)
    {
        lock (_stateLock)
        {
            _lastChange[Key(plc, station)] = Environment.TickCount64;
            _inv.ClearCellLock(plc, station, source, guid, agentTime);
        }
    }

    private void Record(CellLockRequest req, int plc, int station, string result, string message, uint seq)
    {
        var now = _clock();
        var row = new Inventory.CellLockEventRow(now, req.Guid, plc, station, req.Action, result, message, seq, req.AgentTime);
        _recent.Add(row);
        CacheGuid(row);
        _inv.AddCellLockEvent(now, req.Guid, plc, station, req.Action, result, message, seq, req.AgentTime);
    }

    private void CacheGuid(Inventory.CellLockEventRow e)
    {
        lock (_stateLock)
        {
            if (e.Result == "FAIL") return;   // 失败允许同 GUID 重试真正重发
            _guidCache[e.Guid] = e;
            if (_guidCache.Count <= GuidCacheCap) return;
            foreach (var k in _guidCache.OrderBy(kv => kv.Value.Time).Take(_guidCache.Count - GuidCacheCap)
                         .Select(kv => kv.Key).ToList())
                _guidCache.Remove(k);
        }
    }

    private Inventory.CellLockEventRow? FindGuid(string guid)
    {
        lock (_stateLock)
            if (_guidCache.TryGetValue(guid, out var e)) return e;
        return _inv.LastCellLockEvent(guid);
    }

    private static string FmtTime(DateTime? t) =>
        t == null || t.Value == DateTime.MinValue ? "" : t.Value.ToString("yyyy-MM-dd HH:mm:ss");

    // ---------- 解析 ----------
    private static bool TryParse(string body, out CellLockRequest req, out string err)
    {
        req = null!;
        err = "";
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { err = "参数错误：JSON 格式错误"; return false; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { err = "参数错误：JSON 格式错误"; return false; }
            var time = Prop(root, "time");
            var guid = Prop(root, "guid");
            var cms = Prop(root, "cmsIndex");
            var action = Prop(root, "action");

            if (string.IsNullOrWhiteSpace(time)) { err = "参数错误：缺少 time"; return false; }
            if (!TryParseTime(time, out var at)) { err = "参数错误：time 格式非法"; return false; }
            if (string.IsNullOrWhiteSpace(guid)) { err = "参数错误：缺少 guid"; return false; }
            guid = guid.Trim();
            if (guid.Length is < 1 or > 64) { err = "参数错误：guid 非法（1~64 字符）"; return false; }
            if (string.IsNullOrWhiteSpace(cms)) { err = "参数错误：缺少 cmsIndex"; return false; }
            if (string.IsNullOrWhiteSpace(action)) { err = "参数错误：缺少 action"; return false; }
            var act = action.Trim().ToLowerInvariant();
            if (act is not ("lock" or "unlock")) { err = "参数错误：action 非法（lock/unlock）"; return false; }

            req = new CellLockRequest(guid, cms.Trim(), act, at);
            return true;
        }
    }

    /// <summary>属性名大小写不敏感；字符串/数字都接受（cmsIndex 规范是字符串，但容忍数字形态）</summary>
    private static string? Prop(JsonElement root, string name)
    {
        foreach (var p in root.EnumerateObject())
        {
            if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            return p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number => p.Value.GetRawText(),
                _ => null,
            };
        }
        return null;
    }

    private static bool TryParseTime(string s, out DateTime t)
    {
        s = s.Trim();
        if (DateTime.TryParseExact(s, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return true;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t);
    }
}
