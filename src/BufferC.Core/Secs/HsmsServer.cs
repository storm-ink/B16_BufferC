using System.Net;
using System.Net.Sockets;
using BufferC.Core.Config;

namespace BufferC.Core.Secs;

/// <summary>状态数据提供者（S1F3/S1F4 应答数据源）</summary>
public interface IStatusProvider
{
    IReadOnlyList<CarrierStatus> Carriers { get; }
    IReadOnlyList<UnitStatus> Units { get; }
    IReadOnlyList<AlarmStatus> Alarms { get; }
}

public sealed record CarrierStatus(string CarrierId, string CarrierLoc, string InstallTime, ushort State);
public sealed record UnitStatus(string UnitId, ushort State);
public sealed record AlarmStatus(string UnitId, uint AlarmId, string AlarmText, bool Acked = false);

/// <summary>
/// HSMS-SS Passive 服务器（端口 5000），处理 S1/S2/S5/S6/S9 消息。
/// 自研实现（E37.1 帧 + E5 编解码），GEM 状态机应用层自建。
/// </summary>
public sealed class HsmsServer : IDisposable
{
    private readonly HsmsConfig _cfg;
    private readonly IStatusProvider _provider;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly object _sendLock = new();
    private ushort _dataId = 1;
    private ushort _sysBytes = 1;
    private readonly Dictionary<ushort, (string Name, DateTime Deadline)> _outstanding = new();

    public event Action<string, string>? Log;   // (方向, 消息)
    public event Action<string, Dictionary<string, string>>? OnHostCommand; // S2F41 (RCMD, 参数) → 业务层

    public HsmsServer(HsmsConfig cfg, IStatusProvider provider)
    {
        _cfg = cfg;
        _provider = provider;
    }

    public bool Connected => _client?.Connected == true;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _cfg.ListenPort);
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _ = Task.Run(() => T3MonitorAsync(_cts.Token));
        Log?.Invoke("HSMS", $"监听端口 {_cfg.ListenPort}（Passive）");
    }

    public void Stop() => Dispose();

    public void Dispose()
    {
        // 幂等：多次 Dispose 安全（Stop 与测试 DisposeAsync 可能重复调用）
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts != null) { cts.Cancel(); cts.Dispose(); }
        _listener?.Stop();
        _client?.Dispose();
    }

    // ---------- 发送 ----------
    public bool SendS6F11(ushort ceid, ushort rptId, byte[] rptBody)
    {
        var body = SecsEncode.L(
            SecsEncode.U4(_dataId++),
            SecsEncode.U2(ceid),
            SecsEncode.L(SecsEncode.L(SecsEncode.U2(rptId), rptBody)));
        return SendPrimary(6, 11, body);   // S6F11 = Stream 6, Function 11
    }

    public bool SendS5F1(bool set, uint alid, string text)
    {
        var body = SecsEncode.L(
            SecsEncode.B(set ? (byte)0x80 : (byte)0x00),
            SecsEncode.U4(alid),
            SecsEncode.A(text));
        return SendPrimary(5, 1, body);
    }

    public bool SendS1F2() => SendReply(1, 2, SecsEncode.L(SecsEncode.A(_cfg.Mdln), SecsEncode.A(_cfg.SoftRev)));

    public bool SendLinkTest() => SendRaw(0, 1, true, Array.Empty<byte>(), 0);

    // ---------- 接收循环 ----------
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                lock (_sendLock)
                {
                    _client?.Dispose();
                    _client = client;
                    _stream = client.GetStream();
                }
                Log?.Invoke("HSMS", $"MCS 连接建立 {client.Client.RemoteEndPoint}");
                _ = Task.Run(() => ClientLoopAsync(ct));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { Log?.Invoke("HSMS", $"Accept 异常: {e.Message}"); await Task.Delay(500, ct); }
        }
    }

    private async Task ClientLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _stream != null)
            {
                var msg = await ReadMessageAsync(_stream, ct);
                if (msg == null) break;
                Log?.Invoke("←MCS", $"{msg.Name} sys={msg.SystemBytes} HEX={Convert.ToHexString(msg.ToBytes())}\n{msg.Sml()}");
                Dispatch(msg);
            }
        }
        catch (Exception e)
        {
            Log?.Invoke("HSMS", $"连接异常: {e.Message}");
        }
        finally
        {
            lock (_sendLock) { _client?.Dispose(); _client = null; _stream = null; }
            Log?.Invoke("HSMS", "MCS 连接断开 → ControlState=OFFLINE");
        }
    }

    private static async Task<HsmsMessage?> ReadMessageAsync(NetworkStream s, CancellationToken ct)
    {
        var lenBuf = await ReadExactlyAsync(s, 4, ct);
        if (lenBuf == null) return null;
        int len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
        if (len < 10 || len > 4096) throw new FormatException($"非法长度 {len}");
        var frame = await ReadExactlyAsync(s, len, ct);
        return frame == null ? null : HsmsMessage.Parse(frame);
    }

    private static async Task<byte[]?> ReadExactlyAsync(NetworkStream s, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = await s.ReadAsync(buf.AsMemory(got, n - got), ct);
            if (r <= 0) return null;
            got += r;
        }
        return buf;
    }

    // ---------- 分发 ----------
    private void Dispatch(HsmsMessage m)
    {
        if (m.Stream == 0 && m.Function == 1) { SendRaw(0, 2, false, Array.Empty<byte>(), m.SystemBytes); return; }
        if (!m.WBit && _outstanding.Remove(m.SystemBytes))
            Log?.Invoke("MCS→", $"回复 {m.Name} sys={m.SystemBytes}（T3 匹配清除）");
        switch (m.Stream)
        {
            case 1:
                switch (m.Function)
                {
                    case 13: SendReply(1, 14, SecsEncode.L(SecsEncode.B(0), SecsEncode.L()), m); return; // COMMACK=0
                    case 1: SendReply(1, 2, SecsEncode.L(SecsEncode.A(_cfg.Mdln), SecsEncode.A(_cfg.SoftRev)), m); return;
                    case 3: HandleS1F3(m); return;
                    case 15:                                     // OFFLINE（CEID 1 状态迁移事件）
                        SendReply(1, 16, SecsEncode.B(0), m);    // OFLACK
                        SendS6F11(1, 0, SecsEncode.L());
                        return;
                    case 17:                                     // ONLINE REMOTE（CEID 3）
                        SendReply(1, 18, SecsEncode.B(0), m);    // ONLACK
                        SendS6F11(3, 0, SecsEncode.L());
                        return;
                    default: SendS9F5(m); return;
                }
            case 2:
                switch (m.Function)
                {
                    case 31: SendReply(2, 32, SecsEncode.B(0), m); return;  // TIACK
                    case 41: HandleS2F41(m); return;
                    default: SendS9F5(m); return;
                }
            case 5:
                if (m.Function == 5) { HandleS5F5(m); return; }
                if (m.Function == 2) { Log?.Invoke("MCS→", $"S5F2 ACKC5={m.Items().FirstOrDefault()?.AsByte()}"); return; }
                SendS9F5(m); return;
            case 6:
                if (m.Function == 12) { Log?.Invoke("MCS→", "S6F12 ACK"); return; }
                SendS9F5(m); return;
            case 9:
                Log?.Invoke("MCS→", $"S9 错误报告:\n{m.Sml()}"); return;
            case 10:
                if (m.Function == 2) { Log?.Invoke("MCS→", "S10F2 ACK"); return; }
                SendS9F5(m); return;
            default:
                SendS9F3(m); return;
        }
    }

    private void HandleS1F3(HsmsMessage m)
    {
        List<byte[]> values = new();
        try
        {
            foreach (var it in m.Items())
            {
                if (it.Format == SecsFormat.L)
                {
                    foreach (var sv in it.Children ?? new())
                    {
                        values.Add(BuildSvidValue((ushort)sv.AsUInt()));
                    }
                }
                else values.Add(BuildSvidValue((ushort)it.AsUInt()));
            }
        }
        catch (Exception e) { Log?.Invoke("HSMS", $"S1F3 解析失败: {e.Message}"); return; }
        SendReply(1, 4, SecsEncode.L(values.ToArray()), m);
    }

    private byte[] BuildSvidValue(ushort svid) => svid switch
    {
        14 => SecsEncode.U2(5),                                   // ControlState: ONLINE REMOTE
        21 => SecsEncode.U2(3),                                   // SCState: AUTO
        15 => SecsEncode.L(_provider.Carriers.Select(c =>
                  SecsEncode.L(SecsEncode.A(c.CarrierId), SecsEncode.A(c.CarrierLoc),
                               SecsEncode.A(c.InstallTime), SecsEncode.U2(c.State))).ToArray()),
        29 => SecsEncode.L(_provider.Units.Select(u =>
                  SecsEncode.L(SecsEncode.A(u.UnitId), SecsEncode.U2(u.State))).ToArray()),
        3 => SecsEncode.L(_provider.Alarms.Select(a =>
                 SecsEncode.L(SecsEncode.A(a.UnitId), SecsEncode.U4(a.AlarmId), SecsEncode.A(a.AlarmText))).ToArray()),
        _ => SecsEncode.L(),
    };

    private void HandleS2F41(HsmsMessage m)
    {
        string rcmd = "";
        Dictionary<string, string> pars = new();
        try
        {
            var items = m.Items();
            // body = L2[A rcmd, L params]
            if (items.Count > 0)
            {
                var top = items[0].Children;
                if (top is { Count: >= 1 })
                {
                    rcmd = top[0].AsString().Trim();
                    if (top.Count >= 2)
                    {
                        var parsList = top[1].Children;
                        if (parsList != null)
                        {
                            foreach (var pair in parsList)
                            {
                                if (pair.Children != null && pair.Children.Count >= 2)
                                    pars[pair.Children[0].AsString()] = pair.Children[1].AsString();
                            }
                        }
                    }
                }
            }
        }
        catch (Exception e) { Log?.Invoke("HSMS", $"S2F41 解析失败: {e.Message}"); return; }
        Log?.Invoke("←MCS", $"S2F41 RCMD={rcmd} 参数={string.Join(", ", pars.Select(p => $"{p.Key}={p.Value}"))}");
        OnHostCommand?.Invoke(rcmd, pars);
        // 业务层通过事件回调异步处理；先按规范回 HCACK=4（确认将执行）
        byte hcack = rcmd switch
        {
            "PAUSE" or "RESUME" => 2,                             // MCS 已确认不发；异常收到拒收
            "CarrierDataInstall" or "CarrierDataRemove" or "InventoryDataSend" => 4,
            _ => 1,
        };
        SendReply(2, 42, SecsEncode.L(SecsEncode.B(hcack), SecsEncode.L()), m);
    }

    private void HandleS5F5(HsmsMessage m)
    {
        var items = _provider.Alarms.Select(a =>
            SecsEncode.L(SecsEncode.B(0x80), SecsEncode.U4(a.AlarmId), SecsEncode.A(a.AlarmText))).ToArray();
        SendReply(5, 6, SecsEncode.L(items), m);
    }

    private void SendS9F3(HsmsMessage m) => SendRaw(9, 3, false, SecsEncode.B(HeaderOf(m)), m.SystemBytes);
    private void SendS9F5(HsmsMessage m) => SendRaw(9, 5, false, SecsEncode.B(HeaderOf(m)), m.SystemBytes);

    private static byte[] HeaderOf(HsmsMessage m) => m.ToBytes()[4..14];

    // ---------- 原语 ----------
    private bool SendPrimary(byte stream, byte func, byte[] body) => SendRaw(stream, func, true, body, 0);
    private bool SendReply(byte stream, byte func, byte[] body, HsmsMessage to) => SendRaw(stream, func, false, body, to.SystemBytes);
    private bool SendReply(byte stream, byte func, byte[] body) => SendRaw(stream, func, false, body, 0);

    private bool SendRaw(byte stream, byte func, bool wbit, byte[] body, ushort sysBytes)
    {
        lock (_sendLock)
        {
            if (_stream == null) { Log?.Invoke("HSMS", $"发送失败 S{stream}F{func}（未连接）"); return false; }
            if (sysBytes == 0) sysBytes = _sysBytes++;
            var msg = new HsmsMessage { Stream = stream, Function = func, WBit = wbit, SystemBytes = sysBytes, Body = body };
            if (wbit) _outstanding[sysBytes] = (msg.Name, DateTime.UtcNow.AddSeconds(45));
            try
            {
                _stream.Write(msg.ToBytes());
                Log?.Invoke("→MCS", $"{msg.Name} sys={sysBytes}\n{string.Join("\n", msg.Items().Select(x => x.ToSml(1)))}");
                return true;
            }
            catch (Exception e)
            {
                Log?.Invoke("HSMS", $"发送异常: {e.Message}");
                return false;
            }
        }
    }

    private async Task T3MonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);
            var now = DateTime.UtcNow;
            foreach (var (sys, (name, deadline)) in _outstanding.ToList())
            {
                if (now > deadline)
                {
                    Log?.Invoke("HSMS", $"T3 超时: {name} sys={sys} → S9F9");
                    _outstanding.Remove(sys);
                }
            }
        }
    }
}
