using BufferC.Core.Config;
using BufferC.Core.Core;
using BufferC.Core.Modbus;
using BufferC.Harness;
using Xunit;

namespace BufferC.Tests;

/// <summary>Modbus 栈与 PlcSim 的最小验证</summary>
public class ModbusStackTests
{
    [Fact]
    public void PlcSim_BasicPolls_NoDisconnect()
    {
        var plc = new PlcSim(index: 1, unitId: 1);
        int port = plc.Start();
        try
        {
            // 超时放宽：CI 高负载下 3s 可能撞上调度延迟（本测试验证栈正确性，非速度）
            using var client = new ModbusTcpClient("127.0.0.1", port, 1, timeoutMs: 10000);
            client.Connect();
            for (int i = 0; i < 20; i++)
            {
                var r = client.ReadHoldingRegisters(0, 50);
                Assert.Equal(50, r.Length);
                var f = client.ReadHoldingRegisters(306, 35);
                Assert.Equal(35, f.Length);
                for (int s = 0; s < 16; s++)                    // 按站口读（FC03 单次 ≤125）
                {
                    var ids = client.ReadHoldingRegisters((ushort)(50 + s * 16), 16);
                    Assert.Equal(16, ids.Length);
                }
            }
        }
        finally
        {
            plc.Dispose();
        }
    }

    [Fact]
    public void PlcSim_WriteCommand_EchoAndApply()
    {
        var plc = new PlcSim(index: 1, unitId: 1);
        int port = plc.Start();
        try
        {
            using var client = new ModbusTcpClient("127.0.0.1", port, 1, timeoutMs: 10000);
            client.Connect();
            var ids = RegisterMap.PackAscii("CARRIER001", "high");
            client.WriteMultipleRegisters(417 + 2 * 16, ids);   // 站口 3 的 ID
            client.WriteSingleRegister(401 + 2, RegisterMap.CmdWrite);
            client.WriteSingleRegister(400, 1);                  // 触发执行
            Assert.Equal(1, plc.GetEchoNo());
            Assert.Equal(RegisterMap.CmdWrite, plc.GetEchoStation(3));
            Assert.Equal("CARRIER001", plc.GetCarrierId(3));
        }
        finally
        {
            plc.Dispose();
        }
    }

    [Fact]
    public void ChunkedFc16_Writes272Words()
    {
        // FC16 单帧上限 123 字：272 字命令区（401~672）自动分 3 段（123+123+26）逐帧写入
        var plc = new PlcSim(index: 1, unitId: 1);
        int port = plc.Start();
        try
        {
            using var client = new ModbusTcpClient("127.0.0.1", port, 1, timeoutMs: 10000);
            client.Connect();
            var words = new ushort[RegisterMap.RegCmdAreaWords];
            Array.Fill(words, (ushort)0x55);
            client.WriteMultipleRegisters(RegisterMap.RegCmdStation, words);
            Assert.Equal((ushort)0x55, plc.GetReg(401));     // 首
            Assert.Equal((ushort)0x55, plc.GetReg(523));     // 第 1 段尾
            Assert.Equal((ushort)0x55, plc.GetReg(524));     // 第 2 段首
            Assert.Equal((ushort)0x55, plc.GetReg(646));     // 第 2 段尾
            Assert.Equal((ushort)0x55, plc.GetReg(647));     // 第 3 段首
            Assert.Equal((ushort)0x55, plc.GetReg(672));     // 尾
        }
        finally
        {
            plc.Dispose();
        }
    }

    [Fact]
    public void CommandChannel_ClearsStaleArea_BeforeTrigger()
    {
        // PLC 协议（现场确认）：400 新值才扫描 401 以上执行 → 发命令前整段清零 401~672，最后写 400 触发
        var plc = new PlcSim(index: 1, unitId: 1);
        int port = plc.Start();
        try
        {
            using var client = new ModbusTcpClient("127.0.0.1", port, 1, timeoutMs: 10000);
            client.Connect();
            var cfg = new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = port, UnitId = 1, ByteOrder = "high", TimeoutMs = 10000 };
            var app = new BufferCConfig { EchoTimeoutMs = 10000, EchoRetryCount = 0 };
            var ch = new CommandChannel(client, cfg, app);

            // 预埋脏数据：旧命令码/旧 ID 区（真实风险：PLC 非幂等重复执行）
            plc.SetReg(RegisterMap.RegCmdStation, 7);
            plc.SetReg(RegisterMap.RegCmdStation + 5, 3);
            plc.SetReg(500, 0x4142);
            plc.SetReg(672, 9);

            // CmdWrite：整段清零 → ID 就位 → 命令码 → 400 触发
            Assert.True(ch.Execute(3, RegisterMap.CmdWrite, "NEWID01"));
            Assert.True(plc.GetEchoNo() > 0);
            Assert.Equal(RegisterMap.CmdWrite, plc.GetEchoStation(3));
            Assert.Equal("NEWID01", plc.GetCarrierId(3));

            // 全区域扫描 401~672：仅命令码位(401+2)==1 与站口3 ID 区(449~464)非零，其余（含预埋）全 0
            var idWords = RegisterMap.PackAscii("NEWID01", "high");
            for (int addr = RegisterMap.RegCmdStation; addr <= 672; addr++)
            {
                int idOff = addr - (RegisterMap.RegCmdCarrierId + 2 * RegisterMap.CarrierIdWords);
                ushort expected = addr == RegisterMap.RegCmdStation + 2 ? RegisterMap.CmdWrite
                    : (idOff >= 0 && idOff < RegisterMap.CarrierIdWords ? idWords[idOff] : (ushort)0);
                Assert.Equal(expected, plc.GetReg(addr));
            }

            // CmdClear 变体：ID 区清零、命令码=2、其余全 0
            Assert.True(ch.Execute(3, RegisterMap.CmdClear, null));
            Assert.Equal(RegisterMap.CmdClear, plc.GetEchoStation(3));
            Assert.Equal("", plc.GetCarrierId(3));
            for (int addr = RegisterMap.RegCmdStation; addr <= 672; addr++)
            {
                ushort expected = addr == RegisterMap.RegCmdStation + 2 ? RegisterMap.CmdClear : (ushort)0;
                Assert.Equal(expected, plc.GetReg(addr));
            }
        }
        finally
        {
            plc.Dispose();
        }
    }

    [Fact]
    public void CommandChannel_RetryReTriggers400_AfterEchoLost()
    {
        // 回显丢失（PLC 已执行）：区域已就位 → 重试只重写 400 新编号再触发，同一条幂等命令再执行，以回显匹配为唯一成功标准
        var plc = new PlcSim(index: 1, unitId: 1);
        int port = plc.Start();
        try
        {
            using var client = new ModbusTcpClient("127.0.0.1", port, 1, timeoutMs: 10000);
            client.Connect();
            var cfg = new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = port, UnitId = 1, ByteOrder = "high", TimeoutMs = 10000 };
            var app = new BufferCConfig { EchoTimeoutMs = 800, EchoRetryCount = 2 };
            var ch = new CommandChannel(client, cfg, app);

            plc.EchoDropCount = 2;   // 前 2 次执行丢回显 → 第 3 次（仅重写 400）回显正常
            Assert.True(ch.Execute(3, RegisterMap.CmdWrite, "RTY00001"));
            Assert.Equal(RegisterMap.CmdWrite, plc.GetEchoStation(3));
            Assert.True(plc.GetEchoNo() > 0);
            Assert.Equal("RTY00001", plc.GetCarrierId(3));

            // 区域最终状态：仅命令码位与 ID 区非零
            var idWords = RegisterMap.PackAscii("RTY00001", "high");
            for (int addr = RegisterMap.RegCmdStation; addr <= 672; addr++)
            {
                int idOff = addr - (RegisterMap.RegCmdCarrierId + 2 * RegisterMap.CarrierIdWords);
                ushort expected = addr == RegisterMap.RegCmdStation + 2 ? RegisterMap.CmdWrite
                    : (idOff >= 0 && idOff < RegisterMap.CarrierIdWords ? idWords[idOff] : (ushort)0);
                Assert.Equal(expected, plc.GetReg(addr));
            }
        }
        finally
        {
            plc.Dispose();
        }
    }

    [Fact]
    public void CommandChannel_MidSequenceWriteFails_FullRedo()
    {
        // 清零第 1 段写失败（区域未就位）→ 重试必须完整重写（清零→写→触发）
        var plc = new PlcSim(index: 1, unitId: 1);
        int port = plc.Start();
        try
        {
            using var client = new ModbusTcpClient("127.0.0.1", port, 1, timeoutMs: 10000);
            client.Connect();
            var cfg = new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = port, UnitId = 1, ByteOrder = "high", TimeoutMs = 10000 };
            var app = new BufferCConfig { EchoTimeoutMs = 5000, EchoRetryCount = 1 };
            var ch = new CommandChannel(client, cfg, app);

            plc.FailOnWriteNo = 1;    // 第 1 次写（清零第 1 段）返回异常
            Assert.True(ch.Execute(3, RegisterMap.CmdWrite, "FAIL00001"));
            Assert.Equal(RegisterMap.CmdWrite, plc.GetEchoStation(3));
            Assert.Equal("FAIL00001", plc.GetCarrierId(3));

            var idWords = RegisterMap.PackAscii("FAIL00001", "high");
            for (int addr = RegisterMap.RegCmdStation; addr <= 672; addr++)
            {
                int idOff = addr - (RegisterMap.RegCmdCarrierId + 2 * RegisterMap.CarrierIdWords);
                ushort expected = addr == RegisterMap.RegCmdStation + 2 ? RegisterMap.CmdWrite
                    : (idOff >= 0 && idOff < RegisterMap.CarrierIdWords ? idWords[idOff] : (ushort)0);
                Assert.Equal(expected, plc.GetReg(addr));
            }
        }
        finally
        {
            plc.Dispose();
        }
    }

    [Fact]
    public void CommandChannel_TriggerWriteFails_RetryOnlyRewrites400()
    {
        // 清零(3 段)+命令码全部成功、第 5 次写（400 触发）返回异常（PLC 未执行）→ 重试只重写 400 新编号
        var plc = new PlcSim(index: 1, unitId: 1);
        int port = plc.Start();
        try
        {
            using var client = new ModbusTcpClient("127.0.0.1", port, 1, timeoutMs: 10000);
            client.Connect();
            var cfg = new PlcConfig { Index = 1, Ip = "127.0.0.1", Port = port, UnitId = 1, ByteOrder = "high", TimeoutMs = 10000 };
            var app = new BufferCConfig { EchoTimeoutMs = 5000, EchoRetryCount = 1 };
            var ch = new CommandChannel(client, cfg, app);

            plc.FailOnWriteNo = 5;    // 第 5 次写（CmdClear 序列的 400）返回异常 → 第 6 次（重试的 400）正常
            Assert.True(ch.Execute(3, RegisterMap.CmdClear, null));
            Assert.Equal(RegisterMap.CmdClear, plc.GetEchoStation(3));
            Assert.Equal("", plc.GetCarrierId(3));
            for (int addr = RegisterMap.RegCmdStation; addr <= 672; addr++)
            {
                ushort expected = addr == RegisterMap.RegCmdStation + 2 ? RegisterMap.CmdClear : (ushort)0;
                Assert.Equal(expected, plc.GetReg(addr));
            }
        }
        finally
        {
            plc.Dispose();
        }
    }
}
