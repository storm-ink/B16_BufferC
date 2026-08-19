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
    public void RemoveCarrier_ClearsIdSourceAndPending()
    {
        // 取走清 ID（新口径 2026-08-19）：防旧 ID 污染下一次等 ID 判断；历史追溯交给审计/历史表
        var inv = new Inventory(null, 1);
        inv.SetCarrier(1, 3, "CARRIER002", "BUFFER01_03", "MCS");
        Assert.True(inv.RemoveCarrier(1, 3, out var id));
        Assert.Equal("CARRIER002", id);                      // out 参数仍返回旧 ID（上报用）
        Assert.Empty(inv.Carriers);                          // SVID15 不再含
        var row = inv.Ledger.Single(r => r.Station == 3);
        Assert.Equal(RegisterMap.StEmpty, row.State);
        Assert.Equal("", row.CarrierId);
        Assert.Equal("", row.InstallSource);
        Assert.Equal(0, row.CarrierConfirmed);
        Assert.Equal(0, row.PendingCeid);
        Assert.NotEqual("", row.UpdatedAt);
        Assert.False(inv.FindCarrier("CARRIER002", out _, out _));   // 离库不可再 Find
        Assert.False(inv.RemoveCarrier(1, 3, out _));        // 二次移除 false
    }

    [Fact]
    public void RemoveCarrier_TakingState_AlsoRemoves()
    {
        // AGV 取走经 3（正取）：事件在 3→0 周期触发，此时主表状态已同步为 3——3 也算有货可移除
        var inv = new Inventory(null, 1);
        inv.SetCarrier(1, 4, "CARRIER004", "BUFFER01_04", "AGV");
        inv.UpdateStationState(1, 4, RegisterMap.StTaking, 0, 0, "");
        Assert.True(inv.RemoveCarrier(1, 4, out var id));
        Assert.Equal("CARRIER004", id);
        Assert.Equal(RegisterMap.StEmpty, inv.Ledger.Single(r => r.Station == 4).State);
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
    public void UpdateStationState_NoIdBackfill_RaceGuardKeepsCarrier()
    {
        var inv = new Inventory(null, 1);
        // 无变化不写（UpdatedAt 保持空）
        inv.UpdateStationState(1, 1, 0, 0, 0, "");
        Assert.Equal("", inv.Ledger.Single(r => r.Station == 1).UpdatedAt);
        // 载具确认机制（2026-08-19）：ID 只能经 SetCarrier 写入（扫码/AGVC/命令/补填）——快照兜底已移除
        inv.UpdateStationState(1, 1, 1, 0, 0, "SNAP001");
        var row = inv.Ledger.Single(r => r.Station == 1);
        Assert.Equal("", row.CarrierId);
        Assert.Equal(RegisterMap.StHasCarrier, row.State);
        Assert.NotEqual("", row.UpdatedAt);
        // 竞态防护：快照读到 0 但表里有载具 → 状态不覆盖（取走交给事件路径）
        inv.SetCarrier(1, 1, "SNAP001", "BUFFER01_01", "AGV");
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
    public void MarkPlcOffline_SetsAllAvail1_Idempotent_Isolated_Persistent()
    {
        // 断线/启动核对（2026-08-19 用户口径）：只写在服/停服列，变化才写、幂等、多 PLC 隔离、SQLite 持久化
        var db = TempDb();
        try
        {
            var inv = new Inventory(db, 2);                     // 2 台 × 16 站
            inv.UpdateUnit(1, 5, 1);                            // 1:5 先停服
            int n = inv.MarkPlcOffline(1);
            Assert.Equal(15, n);                                // 批量：只写 15 个变化行
            Assert.All(inv.Ledger.Where(r => r.PlcIndex == 1), r => Assert.Equal(1, r.Avail));
            Assert.All(inv.Ledger.Where(r => r.PlcIndex == 2), r => Assert.Equal(0, r.Avail));   // 多 PLC 隔离
            var t1 = inv.Ledger.Single(r => r.PlcIndex == 1 && r.Station == 1).UpdatedAt;
            Assert.NotEqual("", t1);                            // 变化行刷时间
            Assert.Equal(0, inv.MarkPlcOffline(1));             // 幂等：二次 0 变化
            Assert.Equal(t1, inv.Ledger.Single(r => r.PlcIndex == 1 && r.Station == 1).UpdatedAt);   // 不刷时间
            inv.UpdateStationState(1, 1, 0, 0, 0, "");          // 重连自愈路径：快照实况写回
            Assert.Equal(0, inv.Ledger.Single(r => r.PlcIndex == 1 && r.Station == 1).Avail);
            Assert.Equal(2, inv.Units.Single(u => u.UnitId == "BUFFER01_01").State);   // SVID29 口径 2=在服
            inv.Dispose();                                      // 持久化：重启恢复仍是停服
            SqliteConnection.ClearAllPools();
            var inv2 = new Inventory(db, 2);
            Assert.Equal(1, inv2.Ledger.Single(r => r.PlcIndex == 1 && r.Station == 2).Avail);
            Assert.Equal(0, inv2.Ledger.Single(r => r.PlcIndex == 2 && r.Station == 1).Avail);
            inv2.Dispose();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(db)) File.Delete(db);
        }
    }

    [Fact]
    public void MarkPlcOffline_TouchesOnlyAvailColumn()
    {
        // 用户口径 1 的直接编码：只动在服/停服，不动状态/载具/中间态（按真实流程顺序：先登记中间态、后 ID 就绪）
        var inv = new Inventory(null, 1);
        inv.MarkCarrierPending(1, 2, 204);
        inv.SetCarrier(1, 2, "C001", "BUFFER01_02", "AGV");
        inv.MarkPlcOffline(1);
        var row = inv.Ledger.Single(r => r.Station == 2);
        Assert.Equal(1, row.Avail);
        Assert.Equal(RegisterMap.StHasCarrier, row.State);
        Assert.Equal("C001", row.CarrierId);
        Assert.Equal(1, row.CarrierConfirmed);
        Assert.Equal(204, row.PendingCeid);
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

            inv1.Dispose();                                       // 单例连接（D1）：释放文件句柄供重启实例与清理
            SqliteConnection.ClearAllPools();
            var inv2 = new Inventory(db, 1);                       // 重启恢复
            Assert.Equal(16, inv2.Ledger.Count);
            var st2 = inv2.Ledger.Single(r => r.Station == 2);
            Assert.Equal(RegisterMap.StEmpty, st2.State);
            Assert.Equal("", st2.CarrierId);                 // 取走清 ID（新口径 2026-08-19）
            Assert.Equal("", st2.InstallSource);
            Assert.NotEqual("", st2.UpdatedAt);
            var st4 = inv2.Ledger.Single(r => r.Station == 4);
            Assert.Equal(RegisterMap.StPutting, st4.State);
            Assert.Equal(9, st4.AlarmCode);
            Assert.Equal(1, st4.Avail);
            Assert.Single(inv2.Alarms);
            Assert.Equal(1, inv2.ControlState);
            Assert.Equal(3, inv2.ScState);
            inv2.Dispose();
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
        // 第二期异步命令：入队(1) → 执行中(2) → 成功(0)/失败(3)；重启恢复未完成命令 → 4 恢复待校验（B4 重执行）
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

        // 重启恢复（B4 重执行策略）：待执行/执行中 → 4=恢复待校验；校验通过 RequeueCommand → 1
        inv.QueueCommand(1, 4, RegisterMap.CmdWrite, "CARRIER003", "AGV");
        inv.QueueCommand(1, 5, RegisterMap.CmdClear, "CARRIER004", "MCS");
        var stale = inv.RequeueStaleCommands();
        Assert.Equal(2, stale.Count);
        Assert.All(stale, r => Assert.True(r.CmdState is 1 or 2));
        Assert.Equal(4, inv.Ledger.Single(r => r.Station == 4).CmdState);
        Assert.Equal(4, inv.Ledger.Single(r => r.Station == 5).CmdState);
        Assert.Contains(inv.PendingCommands, r => r.Station == 4);   // 恢复待校验同样入队
        inv.RequeueCommand(1, 4);
        Assert.Equal(1, inv.Ledger.Single(r => r.Station == 4).CmdState);
    }

    [Fact]
    public void RecordCommandDone_DirectExecute_RecordsHistory()
    {
        // 取走清理直执行路径：不经队列（PendingCommands 不含）、cmd_state=0/3 + 元数据作最近一条历史（2026-08-19 现场：主表需看到「取走」命令）
        var inv = new Inventory(null, 1);
        inv.RecordCommandDone(1, 2, RegisterMap.CmdClear, "GONE001", "MANUAL", 7, ok: true);
        var row = inv.Ledger.Single(r => r.Station == 2);
        Assert.Equal(0, row.CmdState);
        Assert.Equal(RegisterMap.CmdClear, row.CmdType);
        Assert.Equal("GONE001", row.CmdCarrierId);
        Assert.Equal("MANUAL", row.CmdSource);
        Assert.Equal(7u, row.CmdSeq);
        Assert.NotEqual("", row.CmdTime);
        Assert.Empty(inv.PendingCommands);

        inv.RecordCommandDone(1, 2, RegisterMap.CmdClear, "GONE002", "AGV", 8, ok: false);
        row = inv.Ledger.Single(r => r.Station == 2);
        Assert.Equal(3, row.CmdState);
        Assert.Equal("GONE002", row.CmdCarrierId);
        Assert.Equal("AGV", row.CmdSource);
        Assert.Equal(8u, row.CmdSeq);
    }

    [Fact]
    public void CommandQueue_PersistsAcrossRestart()
    {
        var db = TempDb();
        try
        {
            var inv1 = new Inventory(db, 1);
            inv1.QueueCommand(1, 2, RegisterMap.CmdWrite, "CARRIER001", "MCS");
            inv1.Dispose();
            SqliteConnection.ClearAllPools();
            var inv2 = new Inventory(db, 1);
            var row = inv2.Ledger.Single(r => r.Station == 2);
            Assert.Equal(1, row.CmdState);
            Assert.Equal("CARRIER001", row.CmdCarrierId);
            Assert.Equal("MCS", row.CmdSource);
            var stale = inv2.RequeueStaleCommands();   // 重启恢复路径（B4 重执行）
            Assert.Single(stale);
            Assert.Equal(4, inv2.Ledger.Single(r => r.Station == 2).CmdState);
            inv2.Dispose();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(db)) File.Delete(db);
        }
    }

    [Fact]
    public void History_PersistsAcrossRestart()
    {
        // B3：事件/命令/告警/悬空命令历史四表持久化，重启后 Load* 可读
        var db = TempDb();
        try
        {
            var t = new DateTime(2026, 8, 17, 10, 0, 0, 123);
            var inv1 = new Inventory(db, 1);
            inv1.AddEventHistory(t, 204, "CarrierInstalled");
            inv1.AddCommandHistory(t, "MCS", 1, 2, "CarrierDataInstall", true, 123, "C1");
            inv1.AddCommandHistory(t.AddSeconds(1), "MCS", 1, 3, "CarrierDataRemove", false, 456, "C2");
            inv1.AddAlarmHistory(t, "BUFFER01_01", 9001, "命令执行失败（悬空命令）", "SET");
            inv1.AddFailedHistory(t, "MCS", "CarrierDataInstall", "C2", "BUFFER01_03");
            inv1.Dispose();

            SqliteConnection.ClearAllPools();
            var inv2 = new Inventory(db, 1);
            var evs = inv2.LoadEvents(10);
            Assert.Single(evs);
            Assert.Equal(204, evs[0].Ceid);
            Assert.Equal(t, evs[0].Time);
            var cmds = inv2.LoadCommands(10);
            Assert.Equal(2, cmds.Count);
            Assert.Equal("C1", cmds[0].CarrierId);
            Assert.True(cmds[0].Ok);
            Assert.False(cmds[1].Ok);
            Assert.Equal(3, cmds[1].Station);
            var alarms = inv2.LoadAlarms(10);
            Assert.Single(alarms);
            Assert.Equal("SET", alarms[0].Action);
            Assert.Equal((uint)9001, alarms[0].AlarmId);
            var failed = inv2.LoadFailed(10);
            Assert.Single(failed);
            Assert.Equal("C2", failed[0].CarrierId);
            inv2.Dispose();
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
            inv.Dispose();
            SqliteConnection.ClearAllPools();
            var inv2 = new Inventory(db, 1);
            Assert.Equal("NEW001", inv2.Ledger.Single(r => r.Station == 2).CarrierId);
            inv2.Dispose();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(db)) File.Delete(db);
        }
    }
}
