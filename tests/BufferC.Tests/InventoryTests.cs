using BufferC.Core.Core;
using BufferC.Core.Modbus;
using BufferC.Core.Secs;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BufferC.Tests;

/// <summary>表为中心台账（2026-08-16 重构）：站口主表/告警表/设备状态的单元测试</summary>
public class InventoryTests
{
    private static string TempDb()
    {
        var db = Path.Combine(Path.GetTempPath(), $"inv-test-{Guid.NewGuid():N}.db");
        return db;
    }

    [Fact]
    public void InitStations_BuildsFullRows()
    {
        var inv = new Inventory(null, 2);
        Assert.Equal(32, inv.Ledger.Count);
        var row = inv.Ledger.First();
        Assert.Equal("BUFFER01_01", row.UnitId);
        Assert.Equal(RegisterMap.StEmpty, row.State);
        Assert.Equal("", row.CarrierId);
        Assert.Equal("", row.InstallSource);
    }

    [Fact]
    public void SetCarrier_WritesSource_StateAndUpdatedAt()
    {
        var inv = new Inventory(null, 1);
        inv.SetCarrier(1, 2, "CARRIER001", "BUFFER01_02", "AGV");
        var row = inv.Ledger.Single(r => r.Station == 2);
        Assert.Equal(RegisterMap.StHasCarrier, row.State);
        Assert.Equal("CARRIER001", row.CarrierId);
        Assert.Equal("AGV", row.InstallSource);
        Assert.NotEqual("", row.UpdatedAt);
        Assert.Contains(inv.Carriers, c => c.CarrierId == "CARRIER001" && c.CarrierLoc == "BUFFER01_02" && c.InstallSource == "AGV");
        Assert.True(inv.FindCarrier("CARRIER001", out _, out _));
    }

    [Fact]
    public void RemoveCarrier_MarksDeparted_KeepsHistory()
    {
        var inv = new Inventory(null, 1);
        inv.SetCarrier(1, 3, "CARRIER002", "BUFFER01_03", "MCS");
        Assert.True(inv.RemoveCarrier(1, 3, out var id));
        Assert.Equal("CARRIER002", id);
        Assert.Empty(inv.Carriers);                          // SVID15 不再含
        var row = inv.Ledger.Single(r => r.Station == 3);
        Assert.Equal(RegisterMap.StEmpty, row.State);
        Assert.Equal("CARRIER002", row.CarrierId);           // 历史保留
        Assert.Equal("MCS", row.InstallSource);
        Assert.NotEqual("", row.UpdatedAt);
        Assert.False(inv.FindCarrier("CARRIER002", out _, out _));   // 离库不可再 Find
        Assert.False(inv.RemoveCarrier(1, 3, out _));        // 二次移除 false
    }

    [Fact]
    public void Reinstall_AfterDeparture_OverwritesHistory()
    {
        var inv = new Inventory(null, 1);
        inv.SetCarrier(1, 4, "OLD001", "BUFFER01_04", "AGV");
        var firstTime = inv.Ledger.Single(r => r.Station == 4).UpdatedAt;
        inv.RemoveCarrier(1, 4, out _);
        Thread.Sleep(2);   // UpdatedAt 毫秒精度，确保两次操作时间戳可区分
        inv.SetCarrier(1, 4, "NEW001", "BUFFER01_04", "MANUAL");
        var row = inv.Ledger.Single(r => r.Station == 4);
        Assert.Equal("NEW001", row.CarrierId);               // 覆盖（只保留最近一条）
        Assert.Equal("MANUAL", row.InstallSource);
        Assert.Equal(RegisterMap.StHasCarrier, row.State);
        Assert.NotEqual(firstTime, row.UpdatedAt);
        Assert.Single(inv.Ledger, r => r.Station == 4);
    }

    [Fact]
    public void UpdateStationState_ChangeOnly_FillsId_AndRaceGuard()
    {
        var inv = new Inventory(null, 1);
        // 无变化不写（UpdatedAt 保持空）
        inv.UpdateStationState(1, 1, 0, 0, 0, "");
        Assert.Equal("", inv.Ledger.Single(r => r.Station == 1).UpdatedAt);
        // 有货 + 表无 ID → 快照兜底填 ID
        inv.UpdateStationState(1, 1, 1, 0, 0, "SNAP001");
        var row = inv.Ledger.Single(r => r.Station == 1);
        Assert.Equal("SNAP001", row.CarrierId);
        Assert.Equal(RegisterMap.StHasCarrier, row.State);
        Assert.NotEqual("", row.UpdatedAt);
        // 竞态防护：快照读到 0 但表里有载具 → 状态不覆盖（取走交给事件路径）
        inv.UpdateStationState(1, 1, 0, 0, 0, "");
        Assert.Equal(RegisterMap.StHasCarrier, inv.Ledger.Single(r => r.Station == 1).State);
    }

    [Fact]
    public void UpdateUnit_AvailChange_UpdatesStateAndTime()
    {
        var inv = new Inventory(null, 1);
        inv.UpdateUnit(1, 5, 1);   // 停服
        var row = inv.Ledger.Single(r => r.Station == 5);
        Assert.Equal(1, row.Avail);
        Assert.Equal(1, inv.Units.Single(u => u.UnitId == "BUFFER01_05").State);
        Assert.NotEqual("", row.UpdatedAt);
        inv.UpdateUnit(1, 5, 0);   // 在服
        Assert.Equal(2, inv.Units.Single(u => u.UnitId == "BUFFER01_05").State);
    }

    [Fact]
    public void Alarms_SetClear_DeleteOnClear()
    {
        var inv = new Inventory(null, 1);
        Assert.True(inv.AlarmSet(5, "BUFFER01_01", "Station1 Alarm 5"));
        Assert.False(inv.AlarmSet(5, "BUFFER01_02", "dup"));     // 同告警码不重复
        Assert.Single(inv.Alarms);
        Assert.True(inv.AlarmClear(5));
        Assert.Empty(inv.Alarms);                                 // 清除即删行（方案 A）
        Assert.False(inv.AlarmClear(5));
    }

    [Fact]
    public void DeviceState_DefaultAndSet()
    {
        var inv = new Inventory(null, 1);
        Assert.Equal(5, inv.ControlState);
        Assert.Equal(2, inv.ScState);
        inv.SetDeviceState(1, 2);
        Assert.Equal(1, inv.ControlState);
    }

    [Fact]
    public void Restart_LoadsStationsAlarmsAndDeviceState()
    {
        var db = TempDb();
        try
        {
            var inv1 = new Inventory(db, 1);
            inv1.SetCarrier(1, 2, "CARRIER001", "BUFFER01_02", "AGV");
            inv1.RemoveCarrier(1, 2, out _);                       // 离开行也要恢复
            inv1.AlarmSet(7, "BUFFER01_03", "Alarm7");
            inv1.SetDeviceState(1, 3);
            inv1.UpdateStationState(1, 4, 2, 9, 1, "");            // 正放 + 告警码 + 停服

            SqliteConnection.ClearAllPools();
            var inv2 = new Inventory(db, 1);                       // 重启恢复
            Assert.Equal(16, inv2.Ledger.Count);
            var st2 = inv2.Ledger.Single(r => r.Station == 2);
            Assert.Equal(RegisterMap.StEmpty, st2.State);
            Assert.Equal("CARRIER001", st2.CarrierId);
            Assert.Equal("AGV", st2.InstallSource);
            Assert.NotEqual("", st2.UpdatedAt);
            var st4 = inv2.Ledger.Single(r => r.Station == 4);
            Assert.Equal(RegisterMap.StPutting, st4.State);
            Assert.Equal(9, st4.AlarmCode);
            Assert.Equal(1, st4.Avail);
            Assert.Single(inv2.Alarms);
            Assert.Equal(1, inv2.ControlState);
            Assert.Equal(3, inv2.ScState);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(db)) File.Delete(db);
        }
    }

    [Fact]
    public void CommandQueue_StateMachine_AndStaleRecovery()
    {
        // 第二期异步命令：入队(1) → 执行中(2) → 成功(0)/失败(3)；重启恢复未完成命令标记失败
        var inv = new Inventory(null, 1);
        inv.QueueCommand(1, 2, RegisterMap.CmdWrite, "CARRIER001", "MCS");
        var row = inv.Ledger.Single(r => r.Station == 2);
        Assert.Equal(1, row.CmdState);
        Assert.Equal(RegisterMap.CmdWrite, row.CmdType);
        Assert.Equal("CARRIER001", row.CmdCarrierId);
        Assert.Equal("MCS", row.CmdSource);
        Assert.NotEqual("", row.CmdTime);
        Assert.Single(inv.PendingCommands);

        inv.MarkCommandRunning(1, 2, 42);
        Assert.Equal(2, inv.Ledger.Single(r => r.Station == 2).CmdState);
        Assert.Equal(42u, inv.Ledger.Single(r => r.Station == 2).CmdSeq);
        Assert.Empty(inv.PendingCommands);

        inv.CompleteCommand(1, 2, 42);
        Assert.Equal(0, inv.Ledger.Single(r => r.Station == 2).CmdState);

        inv.QueueCommand(1, 3, RegisterMap.CmdClear, "CARRIER002", "MANUAL");
        inv.FailCommand(1, 3);
        Assert.Equal(3, inv.Ledger.Single(r => r.Station == 3).CmdState);

        // 重启恢复：待执行/执行中 → 标记失败并返回
        inv.QueueCommand(1, 4, RegisterMap.CmdWrite, "CARRIER003", "AGV");
        inv.QueueCommand(1, 5, RegisterMap.CmdClear, "CARRIER004", "MCS");
        var stale = inv.FailStaleCommands();
        Assert.Equal(2, stale.Count);
        Assert.All(stale, r => Assert.True(r.CmdState is 1 or 2));
        Assert.Equal(3, inv.Ledger.Single(r => r.Station == 4).CmdState);
        Assert.Equal(3, inv.Ledger.Single(r => r.Station == 5).CmdState);
    }

    [Fact]
    public void CommandQueue_PersistsAcrossRestart()
    {
        var db = TempDb();
        try
        {
            var inv1 = new Inventory(db, 1);
            inv1.QueueCommand(1, 2, RegisterMap.CmdWrite, "CARRIER001", "MCS");
            SqliteConnection.ClearAllPools();
            var inv2 = new Inventory(db, 1);
            var row = inv2.Ledger.Single(r => r.Station == 2);
            Assert.Equal(1, row.CmdState);
            Assert.Equal("CARRIER001", row.CmdCarrierId);
            Assert.Equal("MCS", row.CmdSource);
            var stale = inv2.FailStaleCommands();   // 重启恢复路径
            Assert.Single(stale);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(db)) File.Delete(db);
        }
    }

    [Fact]
    public void LegacyDb_Ignored_NewTablesCoexist()
    {
        // 旧版 carriers/units 表仍在库中：新实现只读 stations/alarms/device_state，不冲突
        var db = TempDb();
        try
        {
            using (var conn = new SqliteConnection($"Data Source={db}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE carriers(station_key TEXT PRIMARY KEY, carrier_id TEXT, loc TEXT, install_time TEXT, state INTEGER); " +
                    "CREATE TABLE units(unit_id TEXT PRIMARY KEY, state INTEGER); " +
                    "INSERT INTO carriers VALUES('1:2','OLD001','BUFFER01_02','t',1);";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            var inv = new Inventory(db, 1);                        // 旧表数据不加载
            Assert.Equal(16, inv.Ledger.Count);
            Assert.Equal("", inv.Ledger.Single(r => r.Station == 2).CarrierId);
            inv.SetCarrier(1, 2, "NEW001", "BUFFER01_02", "MCS");  // 新表正常读写
            SqliteConnection.ClearAllPools();
            var inv2 = new Inventory(db, 1);
            Assert.Equal("NEW001", inv2.Ledger.Single(r => r.Station == 2).CarrierId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(db)) File.Delete(db);
        }
    }
}
