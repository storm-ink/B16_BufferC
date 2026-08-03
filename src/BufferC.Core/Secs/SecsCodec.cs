using System.Text;

namespace BufferC.Core.Secs;

/// <summary>SECS-II（SEMI E5）格式码</summary>
public enum SecsFormat : byte
{
    L = 0x01, B = 0x25, Boolean = 0x09, A = 0x41,
    U1 = 0xA5, U2 = 0xA9, U4 = 0xA1, I4 = 0x71,
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
            if (Children is null || Children.Count == 0) return $"{pad}<L[0]>";
            var sb = new StringBuilder($"{pad}<L[{Children.Count}]>");
            foreach (var c in Children) sb.Append('\n').Append(c.ToSml(indent + 1));
            return sb.ToString();
        }
        if (Format == SecsFormat.A) return $"{pad}<A \"{AsString()}\">";
        if (Format is SecsFormat.U1 or SecsFormat.U2 or SecsFormat.U4) return $"{pad}<{Format} {AsUInt()}>";
        if (Format == SecsFormat.B) return $"{pad}<B {AsByte()}>";
        if (Format == SecsFormat.Boolean) return $"{pad}<BOOLEAN {Raw[0] != 0}>";
        return $"{pad}<{Format} {Convert.ToHexString(Raw)}>";
    }
}

/// <summary>编码辅助（L/A/B/U1/U2/U4）</summary>
public static class SecsEncode
{
    /// <summary>各格式固有的长度字段字节数（E5：bits 7-6 → 00=1, 01=2, 10=3, 11=8）</summary>
    private static int InherentLenBytes(SecsFormat fmt) => (byte)fmt switch
    {
        0x01 or 0x25 or 0x09 => 1,                              // L / B / BOOLEAN
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
        var body = items.Length == 0 ? Array.Empty<byte>() : items.SelectMany(x => x).ToArray();
        return Head(SecsFormat.L, body.Length).Concat(body).ToArray();
    }

    public static byte[] A(string s)
    {
        var b = Encoding.ASCII.GetBytes(s ?? "");
        return Head(SecsFormat.A, b.Length).Concat(b).ToArray();
    }

    public static byte[] B(params byte[] values) =>
        Head(SecsFormat.B, values.Length).Concat(values).ToArray();

    public static byte[] U1(byte v) => Head(SecsFormat.U1, 1).Append(v).ToArray();
    public static byte[] U2(ushort v) => Head(SecsFormat.U2, 2).Concat(new[] { (byte)(v >> 8), (byte)v }).ToArray();
    public static byte[] U4(uint v) => Head(SecsFormat.U4, 4).Concat(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }).ToArray();
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
        int lenBytes = lenMap[(fmtByte >> 6) & 0x03];
        if (i + lenBytes > buf.Length) throw new FormatException("SECS 长度字段越界");
        int len = 0;
        for (int k = 0; k < lenBytes; k++) len = (len << 8) | buf[i++];
        if (i + len > buf.Length) throw new FormatException($"SECS 数据越界: {fmt} len={len}");
        var raw = buf[i..(i + len)];
        i += len;
        if (fmt == SecsFormat.L)
        {
            var children = new List<SecsItem>();
            int j = 0;
            while (j < raw.Length)
                children.Add(ParseOne(raw, ref j));
            return new SecsItem(fmt, raw, children);
        }
        return new SecsItem(fmt, raw);
    }
}

/// <summary>HSMS-SS 消息（10 字节头 + 正文，单块）</summary>
public sealed class HsmsMessage
{
    public ushort DeviceId { get; set; }
    public bool SBit { get; set; }
    public byte Stream { get; set; }
    public byte Function { get; set; }
    public bool WBit { get; set; }
    public ushort Block { get; set; } = 1;
    public ushort SystemBytes { get; set; }
    public byte[] Body { get; set; } = Array.Empty<byte>();

    public string Name => $"S{Stream}F{Function}" + (WBit ? "W" : "");

    public byte[] ToBytes()
    {
        var h = new byte[10];
        h[0] = (byte)((SBit ? 0x80 : 0) | (DeviceId >> 8));
        h[1] = (byte)DeviceId;
        h[2] = Stream;
        h[3] = Function;
        h[4] = (byte)((WBit ? 0x80 : 0) | (Block >> 8));
        h[5] = (byte)Block;
        h[6] = (byte)(SystemBytes >> 8);
        h[7] = (byte)SystemBytes;
        h[8] = 0;
        h[9] = 0;
        var len = 10 + Body.Length;
        return new byte[] { (byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len }
            .Concat(h).Concat(Body).ToArray();
    }

    public static HsmsMessage Parse(byte[] frame)
    {
        if (frame.Length < 10) throw new FormatException("HSMS 帧过短");
        var m = new HsmsMessage
        {
            SBit = (frame[0] & 0x80) != 0,
            DeviceId = (ushort)(((frame[0] & 0x7F) << 8) | frame[1]),
            Stream = frame[2],
            Function = frame[3],
            WBit = (frame[4] & 0x80) != 0,
            Block = (ushort)(((frame[4] & 0x7F) << 8) | frame[5]),
            SystemBytes = (ushort)((frame[6] << 8) | frame[7]),
        };
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
