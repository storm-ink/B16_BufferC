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
    public void EncodeDecode_Ascii_Has2ByteLengthField()
    {
        // E5：A=0x41 固有 2 字节长度字段（bits 7-6 = 01）
        var bytes = SecsEncode.A("abc");
        Assert.Equal(0x41, bytes[0]);
        Assert.Equal(3, (bytes[1] << 8) | bytes[2]);
        var items = SecsDecode.Parse(bytes);
        Assert.Equal("abc", items[0].AsString());
        // 长字符串同样 2 字节长度字段（无需升级标志）
        var s = new string('X', 300);
        bytes = SecsEncode.A(s);
        Assert.Equal(0x41, bytes[0]);
        Assert.Equal(300, (bytes[1] << 8) | bytes[2]);
    }

    [Fact]
    public void EncodeDecode_U2_Uses3ByteLengthField()
    {
        // E5：U2=0xA9 固有 3 字节长度字段（bits 7-6 = 10）
        var bytes = SecsEncode.U2(204);
        Assert.Equal(0xA9, bytes[0]);
        Assert.Equal(2, (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
        var items = SecsDecode.Parse(bytes);
        Assert.Equal((uint)204, items[0].AsUInt());
        // B=0x25 固有 1 字节长度字段
        var b = SecsEncode.B(0x80);
        Assert.Equal(0x25, b[0]);
        Assert.Equal(1, b[1]);
    }

    [Fact]
    public void HsmsMessage_RoundTrip()
    {
        var m = new HsmsMessage { Stream = 6, Function = 11, WBit = true, SystemBytes = 0x1234, Body = SecsEncode.L(SecsEncode.U4(1), SecsEncode.U2(501)) };
        var parsed = HsmsMessage.Parse(m.ToBytes()[4..]);   // 跳过 4 字节长度
        Assert.Equal(6, parsed.Stream);
        Assert.Equal(11, parsed.Function);
        Assert.True(parsed.WBit);
        Assert.Equal((ushort)0x1234, parsed.SystemBytes);
        Assert.Equal((uint)501, parsed.Items()[0].Children![1].AsUInt());
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
        Assert.Equal(2, set.Count);                          // 102 + 402
        var clear = engine.Diff(Snapshot(0, 0));
        Assert.Equal(2, clear.Count);                        // 101 + 401
    }

    private static PlcSnapshot Snapshot(ushort station1State, ushort station1Alarm)
    {
        var s = new PlcSnapshot { PlcIndex = 1 };
        s.StationState[0] = station1State;
        s.StationAlarm[0] = station1Alarm;
        return s;
    }
}
