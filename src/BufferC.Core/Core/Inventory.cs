using BufferC.Core.Secs;
using Microsoft.Data.Sqlite;

namespace BufferC.Core.Core;

/// <summary>
/// 240 站口库存台账（开发文档 §4.4）。
/// dbPath 为空 = 纯内存；提供 dbPath 时台账同步持久化到 SQLite（重启恢复）。
/// </summary>
public sealed class Inventory
{
    private readonly string? _dbPath;
    private readonly Dictionary<string, CarrierStatus> _byStation = new(); // "plc:station" → 载具
    private readonly Dictionary<string, string> _byCarrier = new();       // carrierId → "plc:station"
    private readonly Dictionary<string, ushort> _unitStates = new();      // UnitId → 状态(1/2)
    private readonly object _lock = new();

    public Inventory(string? dbPath = null)
    {
        _dbPath = dbPath;
        if (dbPath != null) LoadFromDb();
    }

    public IReadOnlyList<CarrierStatus> Carriers { get { lock (_lock) return _byStation.Values.ToList(); } }
    public IReadOnlyList<UnitStatus> Units { get { lock (_lock) return _unitStates.Select(kv => new UnitStatus(kv.Key, kv.Value)).ToList(); } }

    // ---------- 内存操作（同步持久化） ----------
    public void UpdateUnit(int plcIndex, int station, ushort regAvail)  // regAvail: 0=InService 1=OutOfService
    {
        var unitId = EventEngine.Unit(plcIndex, station);
        ushort state = regAvail == 0 ? (ushort)2 : (ushort)1;
        lock (_lock)
        {
            _unitStates[unitId] = state;
            if (_dbPath != null)
                Exec("INSERT OR REPLACE INTO units(unit_id, state) VALUES($u, $s)", ("$u", unitId), ("$s", state));
        }
    }

    public void SetCarrier(int plcIndex, int station, string carrierId, string loc)
    {
        var key = Key(plcIndex, station);
        var time = DateTime.Now.ToString("yyyyMMddHHmmssfff");
        lock (_lock)
        {
            if (_byStation.TryGetValue(key, out var old)) _byCarrier.Remove(old.CarrierId);
            _byStation[key] = new CarrierStatus(carrierId, loc, time, 1);
            _byCarrier[carrierId] = key;
            if (_dbPath != null)
                Exec("INSERT OR REPLACE INTO carriers(station_key, carrier_id, loc, install_time, state) VALUES($k, $c, $l, $t, 1)",
                    ("$k", key), ("$c", carrierId), ("$l", loc), ("$t", time));
        }
    }

    public bool RemoveCarrier(int plcIndex, int station, out string carrierId)
    {
        carrierId = "";
        var key = Key(plcIndex, station);
        lock (_lock)
        {
            if (!_byStation.TryGetValue(key, out var old)) return false;
            carrierId = old.CarrierId;
            _byStation.Remove(key);
            _byCarrier.Remove(carrierId);
            if (_dbPath != null) Exec("DELETE FROM carriers WHERE station_key = $k", ("$k", key));
            return true;
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

    private static string Key(int plcIndex, int station) => $"{plcIndex}:{station}";

    // ---------- SQLite 持久化 ----------
    private void LoadFromDb()
    {
        try
        {
            using var conn = Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS carriers(
                        station_key TEXT PRIMARY KEY, carrier_id TEXT, loc TEXT,
                        install_time TEXT, state INTEGER);
                    CREATE TABLE IF NOT EXISTS units(unit_id TEXT PRIMARY KEY, state INTEGER);
                    """;
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT station_key, carrier_id, loc, install_time, state FROM carriers";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var key = r.GetString(0);
                    _byStation[key] = new CarrierStatus(r.GetString(1), r.GetString(2), r.GetString(3), (ushort)r.GetInt32(4));
                    _byCarrier[r.GetString(1)] = key;
                }
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT unit_id, state FROM units";
                using var r = cmd.ExecuteReader();
                while (r.Read()) _unitStates[r.GetString(0)] = (ushort)r.GetInt32(1);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Inventory] SQLite 加载失败（降级为内存模式）: {e.Message}");
        }
    }

    private void Exec(string sql, params (string Name, object Value)[] pars)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in pars) cmd.Parameters.AddWithValue(name, value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Inventory] SQLite 写入失败: {e.Message}");
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }
}
