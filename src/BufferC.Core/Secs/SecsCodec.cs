using System.Text;

namespace BufferC.Core.Secs;

/// <summary>SECS-II（SEMI E5）格式码</summary>
public enum SecsFormat : byte
{
    L = 0x01, B = 0x21, Boolean = 0x25, A = 0x41,   // 现场 MCS 源码确认的线上格式码（其枚举=本值>>2）
    U1 = 0xA5, U2 = 0xA9, U4 = 0xB1, I4 = 0x71,
}

/// <summary>解码后的数据项</summary>
public sealed class SecsItem
{
    public SecsFormat Format { get; }
    public byte[] Raw { get; }                        // 非 L 类型的负载
    public List<SecsItem>? Children { get; }          // L 类型的子项

    public SecsItem(SecsFormat fmt, byte[] raw, List<SecsItem>? children = null)
    { Format = fmt; Raw = raw; Children = children; }

    public uint AsUInt()
    {
        // 手动大端解析（BitConverter 在 x86 是小端，会读反）
        uint v = 0;
        foreach (var b in Raw) v = (v << 8) | b;
        return v;
    }
    public byte AsByte() => Raw.Length > 0 ? Raw[0] : (byte)0;
    public string AsString() => Encoding.ASCII.GetString(Raw);

    public string ToSml(int indent = 0)
    {
        var pad = new string(' ', indent * 2);
        if (Format == SecsFormat.L)
        {
            if (Children is null || Children.Count == 0) return $"{pad}<L0>";
            // MCS 现场方言：<Ln 开标签、单引号值、无逗号、收尾 > 缩进独占一行
            var sb = new StringBuilder($"{pad}<L{Children.Count}");
            foreach (var c in Children) sb.Append('\n').Append(c.ToSml(indent + 2));
            sb.Append('\n').Append(new string(' ', indent * 2 + 2)).Append('>');
            return sb.ToString();
        }
        if (Format == SecsFormat.A) return $"{pad}<A{Raw.Length} '{AsString()}'>";
        if (Format == SecsFormat.B) return $"{pad}<B{Raw.Length} '{string.Join(" ", Raw.Select(b => b.ToString()))}'>";
        if (Format == SecsFormat.Boolean) return $"{pad}<BOOLEAN{Raw.Length} {(Raw[0] != 0 ? "T" : "F")}>";
        if (Format is SecsFormat.U1 or SecsFormat.U2 or SecsFormat.U4) return $"{pad}<{Format}{Raw.Length} {AsUInt()}>";
        return $"{pad}<{Format}{Raw.Length} {Convert.ToHexString(Raw)}>";
    }
}

/// <summary>编码辅助（L/A/B/U1/U2/U4）</summary>
public static class SecsEncode
{
    /// <summary>各格式固有的长度字段字节数（E5：bits 7-6 → 00=1, 01=2, 10=3, 11=8）</summary>
    private static int InherentLenBytes(SecsFormat fmt) => (byte)fmt switch
    {
        0x01 or 0x21 or 0x25 => 1,                              // L / B / BOOLEAN
        0x41 or 0x65 or 0x69 or 0x71 or 0x79 => 2,              // A / I1 / I2 / I4 / I8
        _ => 3,                                                  // U1/U2/U4/U8/F4/F8
    };

    private static byte[] Head(SecsFormat fmt, int len)
    {
        int fmtByte = (byte)fmt;
        int lenBytes = InherentLenBytes(fmt);
        // E5 位型：00=1字节 01=2字节(A/I) 10=3字节(U/F) 11=8字节。
        // 超长升级必须跳到未被格式码占用的位型：L/B/BOOL(00)→0x81(10,3字节)；A/I(01)→0xC1(11,8字节)；U/F(10)→0xE0|code(11,8字节)
        if (lenBytes == 1 && len >= 256) { fmtByte |= 0x80; lenBytes = 3; }
        else if (lenBytes == 2 && len >= 65536) { fmtByte |= 0x80; lenBytes = 8; }
        else if (lenBytes == 3 && len >= (1 << 24)) { fmtByte |= 0x40; lenBytes = 8; }
        var b = new byte[1 + lenBytes];
        b[0] = (byte)fmtByte;
        for (int k = 0; k < lenBytes; k++) b[1 + k] = (byte)((long)len >> (8 * (lenBytes - 1 - k)));
        return b;
    }

    public static byte[] L(params byte[][] items)
    {
        // E5：L 的长度字段 = 元素个数（不是字节数！现场 MCS 实测：01 02 = 2 个元素）
        // 元素数 >255 升级 3 字节长度（0x81）
        var body = items.Length == 0 ? Array.Empty<byte>() : items.SelectMany(x => x).ToArray();
        int n = items.Length;
        byte[] head = n < 256
            ? new byte[] { 0x01, (byte)n }
            : new byte[] { 0x81, (byte)(n >> 16), (byte)(n >> 8), (byte)n };
        return head.Concat(body).ToArray();
    }

    public static byte[] A(string s)
    {
        // MCS 现场方言：A 长度字段 1 字节（41 07 ...，不是 E5 标准的 41 00 07）
        var b = Encoding.ASCII.GetBytes(s ?? "");
        if (b.Length > 255) throw new FormatException($"A 长度超过 255: {b.Length}");
        return new byte[] { 0x41, (byte)b.Length }.Concat(b).ToArray();
    }

    public static byte[] B(params byte[] values) =>
        Head(SecsFormat.B, values.Length).Concat(values).ToArray();

    // MCS 现场方言：U 类长度字段 1 字节 = 字节数（A9 02 00 15 = 2 字节 1 个 U2 值）
    private static byte[] U(byte fmt, byte[] data)
    {
        if (data.Length > 255) throw new FormatException($"{fmt:X2} 数据超过 255 字节");
        return new byte[] { fmt, (byte)data.Length }.Concat(data).ToArray();
    }
    public static byte[] U1(byte v) => U(0xA5, new[] { v });
    public static byte[] U2(ushort v) => U(0xA9, new[] { (byte)(v >> 8), (byte)v });
    public static byte[] U4(uint v) => U(0xB1, new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
}

/// <summary>解码</summary>
public static class SecsDecode
{
    public static List<SecsItem> Parse(byte[] buf)
    {
        var items = new List<SecsItem>();
        int i = 0;
        while (i < buf.Length)
            items.Add(ParseOne(buf, ref i));
        return items;
    }

    private static SecsItem ParseOne(byte[] buf, ref int i)
    {
        byte fmtByte = buf[i++];
        // 格式识别：L=0x01（低6位=1 且 bit6=0；A=0x41 的 bit6=1，0x81/0xC1 是 L 的升级位型）
        bool isList = (fmtByte & 0x3F) == 0x01 && (fmtByte & 0x40) == 0;
        var fmt = isList ? SecsFormat.L : (SecsFormat)fmtByte;
        // E5 长度规则：bits 7-6 → 00=1 字节, 01=2 字节, 10=3 字节, 11=8 字节
        int[] lenMap = { 1, 2, 3, 8 };
        // MCS 现场方言：A/U/I 长度字段固定 1 字节（41 07 ... / A9 02 ...）
        int lenBytes = fmt is SecsFormat.A or SecsFormat.U1 or SecsFormat.U2 or SecsFormat.U4 or SecsFormat.I4
            ? 1 : lenMap[(fmtByte >> 6) & 0x03];
        if (i + lenBytes > buf.Length) throw new FormatException("SECS 长度字段越界");
        int len = 0;
        for (int k = 0; k < lenBytes; k++) len = (len << 8) | buf[i++];
        if (fmt == SecsFormat.L)
        {
            // E5：L 的长度 = 元素个数，逐项解析 len 个元素（现场 MCS 语义）
            var children = new List<SecsItem>();
            for (int k = 0; k < len && i < buf.Length; k++)
                children.Add(ParseOne(buf, ref i));
            return new SecsItem(fmt, Array.Empty<byte>(), children);
        }
        if (i + len > buf.Length) throw new FormatException($"SECS 数据越界: {fmt} len={len}");
        var raw = buf[i..(i + len)];
        i += len;
        return new SecsItem(fmt, raw);
    }
}

/// <summary>
/// HSMS-SS 消息（SEMI E37 头 + 正文，单块）：
/// SessionID(2) + Stream/Function(2) + PType(1) + SType(1) + SystemBytes(4)。
/// 控制消息（Stream==0）：字节 2/3=0，SType=Function（1=Select.req 2=Select.rsp 3=Deselect.req
/// 4=Deselect.rsp 5=Linktest.req 6=Linktest.rsp 7=Reject.req 9=Separate.req）。
/// W 位在流字节 bit7（现场 MCS 实测 S1F13W 流字节 0x81，镜像其约定）。
/// </summary>
public sealed class HsmsMessage
{
    public ushort SessionId { get; set; } = 0xFFFF;
    public byte Stream { get; set; }
    public byte Function { get; set; }
    public bool WBit { get; set; }
    public uint SystemBytes { get; set; }
    public byte[] Body { get; set; } = Array.Empty<byte>();

    public string Name => $"S{Stream}F{Function}" + (WBit ? "W" : "");

    public byte[] ToBytes()
    {
        bool control = Stream == 0;
        var h = new byte[10];
        h[0] = (byte)(SessionId >> 8);
        h[1] = (byte)SessionId;
        h[2] = control ? (byte)0 : (byte)(Stream | (WBit ? 0x80 : 0));
        h[3] = control ? (byte)0 : Function;
        h[4] = 0;                                   // PType = 0（SECS-II 编码）
        h[5] = (byte)(control ? Function : 0);      // SType：控制=控制码，数据=0
        h[6] = (byte)(SystemBytes >> 24);
        h[7] = (byte)(SystemBytes >> 16);
        h[8] = (byte)(SystemBytes >> 8);
        h[9] = (byte)SystemBytes;
        var len = 10 + Body.Length;
        return new byte[] { (byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len }
            .Concat(h).Concat(Body).ToArray();
    }

    public static HsmsMessage Parse(byte[] frame)
    {
        if (frame.Length < 10) throw new FormatException("HSMS 帧过短");
        var m = new HsmsMessage
        {
            SessionId = (ushort)((frame[0] << 8) | frame[1]),
            SystemBytes = (uint)((frame[6] << 24) | (frame[7] << 16) | (frame[8] << 8) | frame[9]),
        };
        bool control = frame[2] == 0 && frame[3] == 0 && frame[4] == 0 && frame[5] is >= 1 and <= 9;
        if (control)
        {
            m.Stream = 0;
            m.Function = frame[5];                  // 控制消息：SType 映射到 Function
        }
        else
        {
            m.Stream = (byte)(frame[2] & 0x7F);
            m.Function = (byte)(frame[3] & 0x7F);   // 掩码保留：MCS 现发的功能字节也带 0x80（81 83 / 82 9F）
            m.WBit = (frame[2] & 0x80) != 0;
        }
        m.Body = frame.Length > 10 ? frame[10..] : Array.Empty<byte>();
        return m;
    }

    public List<SecsItem> Items() => SecsDecode.Parse(Body);

    public string Sml()
    {
        try { return string.Join("\n", Items().Select(x => x.ToSml(1))); }
        catch (Exception e) { return $"解析失败: {e.Message}"; }
    }
}
