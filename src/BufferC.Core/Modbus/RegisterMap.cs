namespace BufferC.Core.Modbus;

/// <summary>V8 寄存器表（唯一事实源：开发文档 §2.2）</summary>
public static class RegisterMap
{
    public const int RegBufferNo = 0;          // Buffer 编号 1~15
    public const int RegAlarmSummary = 1;      // 告警汇总 0/1
    public const int RegStationState = 2;      // 站口 1~16 货物状态（2~17）
    public const int RegStationAlarm = 18;     // 站口 1~16 告警码（18~33）
    public const int RegStationAvail = 34;     // 站口 1~16 可用状态（34~49）
    public const int RegCarrierId = 50;        // 站口 1~16 货物ID（50~305，每站 16 字）
    public const int RegEchoNo = 306;          // 命令编号回显
    public const int RegEchoStation = 307;     // 站口命令回显（307~322）
    public const int RegScanStation = 323;     // 扫码站口号 1~16
    public const int RegScanCode = 324;        // 货物扫码号（324~339）
    public const int RegHandshake = 340;       // 握手：PLC 写 1=请求，BufferC 写 0=应答
    public const int RegCmdNo = 400;           // 命令编号（写，1~65535 自增）
    public const int RegCmdStation = 401;      // 站口命令（401~416）
    public const int RegCmdCarrierId = 417;    // 货物ID 写入（417~672）
    public const int RegCmdAreaWords = 272;    // 命令区总长 401~672（发命令前整段清零，400 变化才触发 PLC）

    public const int StationsPerPlc = 16;
    public const int CarrierIdWords = 16;      // 32 字符 ASCII
    public const int CarrierIdChars = 32;

    public const ushort CmdNone = 0;
    public const ushort CmdWrite = 1;          // 写入货物ID
    public const ushort CmdClear = 2;          // 清除货物ID

    // 站口状态
    public const ushort StEmpty = 0;
    public const ushort StHasCarrier = 1;      // 有货已存储（AGV 放入）
    public const ushort StPutting = 2;         // AGV 正在放入
    public const ushort StTaking = 3;          // AGV 正在取出
    public const ushort StFault = 4;
    public const ushort StManualCarrier = 5;   // （人工）有货已存储（长保持；取货方式看路径：经3=AGV取走/直跳0=人工取走）

    /// <summary>32 字符 ASCII ↔ 16 个 WORD（字节序按配置）</summary>
    public static ushort[] PackAscii(string s, string byteOrder)
    {
        var b = new byte[CarrierIdChars];
        var raw = System.Text.Encoding.ASCII.GetBytes(s ?? "");
        Array.Copy(raw, b, Math.Min(raw.Length, CarrierIdChars));
        var words = new ushort[CarrierIdWords];
        bool highFirst = byteOrder != "low";
        for (int i = 0; i < CarrierIdWords; i++)
        {
            int lo = b[i * 2], hi = b[i * 2 + 1];
            words[i] = highFirst ? (ushort)((hi << 8) | lo) : (ushort)((lo << 8) | hi);
        }
        return words;
    }

    public static string UnpackAscii(ushort[] words, string byteOrder)
    {
        var b = new byte[CarrierIdChars];
        bool highFirst = byteOrder != "low";
        for (int i = 0; i < Math.Min(words.Length, CarrierIdWords); i++)
        {
            b[i * 2] = highFirst ? (byte)(words[i] & 0xFF) : (byte)(words[i] >> 8);
            b[i * 2 + 1] = highFirst ? (byte)(words[i] >> 8) : (byte)(words[i] & 0xFF);
        }
        return System.Text.Encoding.ASCII.GetString(b).TrimEnd('\0').TrimEnd();
    }

}
