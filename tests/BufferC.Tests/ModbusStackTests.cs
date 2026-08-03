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
}
