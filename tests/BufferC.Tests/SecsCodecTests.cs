using BufferC.Core.Core;
using BufferC.Core.Modbus;
using BufferC.Core.Secs;
using Xunit;

namespace BufferC.Tests;

/// <summary>SECS-II 编解码 round-trip 单测</summary>
public class SecsCodecTests
{
    [Fact]
    public void EncodeDecode_A_String()
    {
        var bytes = SecsEncode.A("CARRIER001");
        var items = SecsDecode.Parse(bytes);
        Assert.Single(items);
        Assert.Equal(SecsFormat.A, items[0].Format);
        Assert.Equal("CARRIER001", items[0].AsString());
    }

    [Fact]
    public void EncodeDecode_U2_U4()
    {
        var bytes = SecsEncode.L(SecsEncode.U2(204), SecsEncode.U4(4294967295));
        var items = SecsDecode.Parse(bytes);
        Assert.Single(items);                             // L 包装
        var children = items[0].Children!;
        Assert.Equal(2, children.Count);
        Assert.Equal((uint)204, children[0].AsUInt());
        Assert.Equal(4294967295u, children[1].AsUInt());
    }

    [Fact]
    public void EncodeDecode_List_Nested()
    {
        // L3[U4 dataid, U2 ceid, L1[L2[U2 rptid, L2[A id, A loc]]]]
        var bytes = SecsEncode.L(
            SecsEncode.U4(1),
            SecsEncode.U2(204),
            SecsEncode.L(SecsEncode.L(SecsEncode.U2(2), SecsEncode.L(SecsEncode.A("ABC"), SecsEncode.A("Buffer1_Port3")))));
        var items = SecsDecode.Parse(bytes);
        Assert.Single(items);                                  // L 包装
        var children = items[0].Children!;
        Assert.Equal(3, children.Count);
        Assert.Equal((uint)1, children[0].AsUInt());
        Assert.Equal((uint)204, children[1].AsUInt());
        var rptBody = children[2].Children![0].Children![1];
        Assert.Equal("ABC", rptBody.Children![0].AsString());
        Assert.Equal("Buffer1_Port3", rptBody.Children![1].AsString());
    }

    [Fact]
    public void EncodeDecode_Ascii_Has1ByteLengthField()
    {
        // MCS 现场方言：A=0x41 长度字段 1 字节（41 07 ...，非 E5 的 41 00 07）
        var bytes = SecsEncode.A("abc");
        Assert.Equal(0x41, bytes[0]);
        Assert.Equal(3, bytes[1]);
        var items = SecsDecode.Parse(bytes);
        Assert.Equal("abc", items[0].AsString());
        // 超过 255 字符超出 1 字节长度字段，应拒绝
        Assert.Throws<FormatException>(() => SecsEncode.A(new string('X', 300)));
    }

    [Fact]
    public void EncodeDecode_U2_Uses1ByteLengthField()
    {
        // MCS 现场方言：U2=0xA9 长度字段 1 字节=字节数（A9 02 00 CC = 1 个值 204）
        var bytes = SecsEncode.U2(204);
        Assert.Equal(0xA9, bytes[0]);
        Assert.Equal(2, bytes[1]);
        var items = SecsDecode.Parse(bytes);
        Assert.Equal((uint)204, items[0].AsUInt());
        // B=0x21 固有 1 字节长度字段（现场 MCS 源码确认）
        var b = SecsEncode.B(0x80);
        Assert.Equal(0x21, b[0]);
        Assert.Equal(1, b[1]);
    }

    [Fact]
    public void HsmsMessage_Parse_RealMcs_S1F3W()
    {
        // 现场 MCS 实测帧：S1F3W 查 SVID21（SCState）——L[1 元素][U2 长度1字节=2字节, 值 0x0015=21]
        var m = HsmsMessage.Parse(Convert.FromHexString("000181030000000000050101A9020015"));
        Assert.Equal(1, m.Stream);
        Assert.Equal(3, m.Function);
        Assert.True(m.WBit);
        Assert.Equal(5u, m.SystemBytes);
        var items = m.Items();
        Assert.Single(items);
        Assert.Equal(21u, items[0].Children![0].AsUInt());
    }

    [Fact]
    public void HsmsMessage_RoundTrip()
    {
        var m = new HsmsMessage { Stream = 6, Function = 11, WBit = true, SystemBytes = 0x1234, Body = SecsEncode.L(SecsEncode.U4(1), SecsEncode.U2(501)) };
        var parsed = HsmsMessage.Parse(m.ToBytes()[4..]);   // 跳过 4 字节长度
        Assert.Equal(6, parsed.Stream);
        Assert.Equal(11, parsed.Function);
        Assert.True(parsed.WBit);
        Assert.Equal(0x1234u, parsed.SystemBytes);
        Assert.Equal((uint)501, parsed.Items()[0].Children![1].AsUInt());
    }

    [Fact]
    public void HsmsMessage_Parse_RealMcs_SelectReq()
    {
        // 现场 MCS 实测帧：Select.req（SessionID=FFFF, SType=1, sys=0）
        var m = HsmsMessage.Parse(Convert.FromHexString("FFFF0000000100000000"));
        Assert.Equal(0xFFFF, m.SessionId);
        Assert.Equal(0, m.Stream);
        Assert.Equal(1, m.Function);
        Assert.Equal(0u, m.SystemBytes);
        // 应答 Select.rsp 按 E37 编码：字节2/3=0，SType=2
        var rsp = new HsmsMessage { SessionId = 0xFFFF, Stream = 0, Function = 2, SystemBytes = 0 }.ToBytes()[4..];
        Assert.Equal("FFFF0000000200000000", Convert.ToHexString(rsp));
    }

    [Fact]
    public void HsmsMessage_Parse_RealMcs_S1F13W()
    {
        // 现场 MCS 实测帧：S1F13W（SessionID=1, 流字节 0x81=W位+S1, sys=0x100, 正文 L[0]）
        var m = HsmsMessage.Parse(Convert.FromHexString("0001810D0000000001000100"));
        Assert.Equal(1, m.SessionId);
        Assert.Equal(1, m.Stream);
        Assert.Equal(13, m.Function);
        Assert.True(m.WBit);
        Assert.Equal(0x100u, m.SystemBytes);
        Assert.Empty(m.Items()[0].Children!);               // L[0]
        // 应答 S1F14：会话/系统字节原样回显，E37 数据头（字节4/5=0）
        // 正文按 MCS 现场方言：01 02 21 01 00 01 02 41 07 "BUFFERC" 41 05 "0.1.0"
        // （L 长度=元素个数 2；B=0x21；A 长度 1 字节）
        var body = SecsEncode.L(SecsEncode.B(0), SecsEncode.L(SecsEncode.A("BUFFERC"), SecsEncode.A("0.1.0")));
        Assert.Equal("010221010001024107425546464552434105302E312E30", Convert.ToHexString(body));
        var rsp = new HsmsMessage { SessionId = 1, Stream = 1, Function = 14, SystemBytes = 0x100, Body = body }.ToBytes()[4..];
        Assert.Equal("0001010E000000000100", Convert.ToHexString(rsp[..10]));
    }

    [Fact]
    public void SecsFormat_WireCodes_MatchMcsSource()
    {
        // 现场 MCS 源码（枚举 = 线上字节 >> 2）确认的格式码表
        Assert.Equal(0x01, (byte)SecsFormat.L);
        Assert.Equal(0x21, (byte)SecsFormat.B);
        Assert.Equal(0x25, (byte)SecsFormat.Boolean);
        Assert.Equal(0x41, (byte)SecsFormat.A);
        Assert.Equal(0xA5, (byte)SecsFormat.U1);
        Assert.Equal(0xA9, (byte)SecsFormat.U2);
        Assert.Equal(0xB1, (byte)SecsFormat.U4);
        Assert.Equal(0x71, (byte)SecsFormat.I4);
    }

    [Fact]
    public void RegisterMap_PackUnpack_Ascii()
    {
        var words = RegisterMap.PackAscii("CARRIER001", "high");
        Assert.Equal(16, words.Length);
        Assert.Equal("CARRIER001", RegisterMap.UnpackAscii(words, "high"));
        // 两种字节序往返一致
        var wordsLow = RegisterMap.PackAscii("CARRIER001", "low");
        Assert.Equal("CARRIER001", RegisterMap.UnpackAscii(wordsLow, "low"));
        // 32 字符截断
        Assert.Equal(32, RegisterMap.UnpackAscii(RegisterMap.PackAscii(new string('A', 40), "high"), "high").Length);
    }

    [Fact]
    public void EventEngine_Diff_AgvInstall_0_2_1()
    {
        var engine = new EventEngine();
        var s1 = Snapshot(0, 0);
        Assert.Empty(engine.Diff(s1));                       // 基线
        var s2 = Snapshot(2, 0);
        Assert.Empty(engine.Diff(s2));                       // 正放中间态
        var s3 = Snapshot(1, 0);
        s3.CarrierId[0] = "CARRIER001";
        var events = engine.Diff(s3);                        // 放完 → 204
        var ev = Assert.Single(events);
        Assert.Equal(EventKind.CarrierInstalled, ev.Kind);
        Assert.Equal("CARRIER001", ev.Params["CarrierID"]);
    }

    [Fact]
    public void EventEngine_Diff_Alarm_Set_Clear()
    {
        var engine = new EventEngine();
        engine.Diff(Snapshot(0, 0));
        var set = engine.Diff(Snapshot(0, 5));
        // 一个事件 = 一组三帧（置起 S5F1+102+402 / 清除 S5F1+101+401），由 BufferCService 在组锁内连发（现场要求 2026-08-17）
        var ev = Assert.Single(set);
        Assert.Equal(EventKind.AlarmSet, ev.Kind);
        var clear = engine.Diff(Snapshot(0, 0));
        var evc = Assert.Single(clear);
        Assert.Equal(EventKind.AlarmCleared, evc.Kind);
    }

    private static PlcSnapshot Snapshot(ushort station1State, ushort station1Alarm)
    {
        var s = new PlcSnapshot { PlcIndex = 1 };
        s.StationState[0] = station1State;
        s.StationAlarm[0] = station1Alarm;
        return s;
    }
}
