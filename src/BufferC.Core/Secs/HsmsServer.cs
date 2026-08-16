using System.Net;
using System.Net.Sockets;
using BufferC.Core.Config;
using BufferC.Core.Core;

namespace BufferC.Core.Secs;

/// <summary>状态数据提供者（S1F3/S1F4 应答数据源）——表为中心：全部从 Inventory 表读</summary>
public interface IStatusProvider
{
    IReadOnlyList<CarrierStatus> Carriers { get; }
    IReadOnlyList<UnitStatus> Units { get; }
    IReadOnlyList<AlarmStatus> Alarms { get; }
    ushort ControlState { get; }    // SVID 14（设备状态表）
    ushort ScState { get; }         // SVID 21（设备状态表）
}

/// <summary>载具行（SVID 15 投影）：UpdatedAt=数据更新时间（合并安装/离开时间，现场约定）</summary>
public sealed record CarrierStatus(string CarrierId, string CarrierLoc, string UpdatedAt, ushort State,
    string InstallSource = "");
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
    private ushort _sessionId = 0xFFFF;              // E37 会话：以最后收到的消息为准回显
    private uint _dataId = 1;
    private uint _sysBytes = 1;
    private readonly object _outstandingLock = new();   // W 位事务表三线程访问（发送加/接收删/T3 监控遍历删）：统一加锁，防 Dictionary 并发损坏
    private readonly Dictionary<uint, (string Name, DateTime Deadline)> _outstanding = new();

    public event Action<string, string, LogLevel>? Log;   // (方向, 消息, 级别)
    /// <summary>审计（收发摘要/S2F41 参数，不受 logFrames 开关约束；证据留存用）</summary>
    public event Action<string, string>? Audit;
    public event Action<string, Dictionary<string, string>, List<SecsItem>>? OnHostCommand; // S2F41 (RCMD, 参数, 原始条目) → 业务层

    /// <summary>帧日志运行时开关（联调 Web /api/debug 可切换；初始取自 config logFrames）</summary>
    public volatile bool LogFramesEnabled;

    /// <summary>HSMS 收发记录（MCS 联调页观察）：每条真实报文——方向/消息名/sys/W位/HEX/SML（帧日志关时 HEX/SML 为空，省转换）</summary>
    public sealed record HsmsTrafficEntry(DateTime Time, string Direction, string Name, uint SystemBytes, bool WBit, string Hex, string Sml);
    public readonly RingBuffer<HsmsTrafficEntry> Traffic = new(200);

    /// <summary>协议帧级日志（logFrames=false 时跳过，避免运行期刷屏）</summary>
    private void LogFrame(string category, string msg, LogLevel level = LogLevel.Info)
    {
        if (!LogFramesEnabled) return;
        Log?.Invoke(category, msg, level);
    }

    /// <summary>SML 树折叠为单行（一行一条，保证 grep/脚本可靠解析）</summary>
    private static string Fold(string s) => s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    public HsmsServer(HsmsConfig cfg, IStatusProvider provider)
    {
        _cfg = cfg;
        _provider = provider;
        LogFramesEnabled = cfg.LogFrames;
    }

    public bool Connected => _client?.Connected == true;

    // ---------- 运行统计（界面/联调用） ----------
    public DateTime? EstablishedAt;
    public string PeerEndpoint = "";
    public long MessagesIn;
    public long MessagesOut;
    public long SendFailCount;
    public long T3TimeoutCount;
    public string PeerMdln = "";
    public string PeerSoftRev = "";

    public sealed record McsStats(bool Connected, DateTime? EstablishedAt, string PeerEndpoint,
        long MessagesIn, long MessagesOut, long SendFail, long T3Timeout, string PeerMdln, string PeerSoftRev);

    public McsStats GetStats() => new(Connected, EstablishedAt, PeerEndpoint,
        MessagesIn, MessagesOut, SendFailCount, T3TimeoutCount, PeerMdln, PeerSoftRev);

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _cfg.ListenPort);
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _ = Task.Run(() => T3MonitorAsync(_cts.Token));
        Log?.Invoke("HSMS", $"监听端口 {_cfg.ListenPort}（Passive）", LogLevel.Info);
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
    /// <summary>S6F11 事件上报。rptBody=null 时第三项发空列表 L0（MCS 现场方言：L3[U4, U2, L0]，状态类事件用）</summary>
    public bool SendS6F11(ushort ceid, ushort rptId, byte[]? rptBody)
    {
        var body = rptBody == null
            ? SecsEncode.L(SecsEncode.U4(_dataId++), SecsEncode.U2(ceid), SecsEncode.L())
            : SecsEncode.L(SecsEncode.U4(_dataId++), SecsEncode.U2(ceid),
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

    public bool SendLinkTest() => SendRaw(0, 5, false, Array.Empty<byte>(), 0);   // S0F5 Linktest.req

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
                EstablishedAt = DateTime.Now;
                PeerEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "";
                Log?.Invoke("HSMS", $"MCS 连接建立 {PeerEndpoint}", LogLevel.Info);
                _ = Task.Run(() => ClientLoopAsync(ct));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { Log?.Invoke("HSMS", $"Accept 异常: {e.Message}", LogLevel.Warn); await Task.Delay(500, ct); }
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
                MessagesIn++;
                string hex = "", sml = "";
                if (LogFramesEnabled) { hex = Convert.ToHexString(msg.ToBytes()); sml = msg.Sml(); }
                Traffic.Add(new HsmsTrafficEntry(DateTime.Now, "收", msg.Name, msg.SystemBytes, msg.WBit, hex, sml));
                if (LogFramesEnabled)
                    Log?.Invoke("←MCS", $"{msg.Name} sys={msg.SystemBytes} HEX={hex} SML={Fold(sml)}", LogLevel.Info);
                Audit?.Invoke("←MCS", $"{msg.Name} sys={msg.SystemBytes}{(msg.WBit ? " W" : "")}");
                Dispatch(msg);
            }
        }
        catch (Exception e)
        {
            Log?.Invoke("HSMS", $"连接异常: {e.Message}", LogLevel.Error);
        }
        finally
        {
            lock (_sendLock) { _client?.Dispose(); _client = null; _stream = null; }
            Log?.Invoke("HSMS", "MCS 连接断开 → ControlState=OFFLINE", LogLevel.Warn);
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
        _sessionId = m.SessionId;    // E37：回显最后收到的会话号（Select.req 的 FFFF / 数据消息的实际会话）
        if (m.Stream == 0)
        {
            switch (m.Function)
            {
                case 1: SendRaw(0, 2, false, Array.Empty<byte>(), m.SystemBytes); return;   // Select.rsp
                case 3: SendRaw(0, 4, false, Array.Empty<byte>(), m.SystemBytes); return;   // Deselect.rsp
                case 5: SendRaw(0, 6, false, Array.Empty<byte>(), m.SystemBytes); return;   // Linktest.rsp
                case 7: LogFrame("MCS→", "Reject.req（忽略）"); return;
                case 9:                                                                     // Separate.req → 对方主动断开
                    Log?.Invoke("HSMS", "MCS 发送 Separate → 关闭连接", LogLevel.Info);
                    lock (_sendLock) { _client?.Dispose(); }
                    return;
                default: return;                                                            // 其余控制消息忽略
            }
        }
        bool matched = false;
        if (!m.WBit) lock (_outstandingLock) matched = _outstanding.Remove(m.SystemBytes);
        if (matched)
            LogFrame("MCS→", $"回复 {m.Name} sys={m.SystemBytes}（T3 匹配清除）");
        switch (m.Stream)
        {
            case 1:
                switch (m.Function)
                {
                    case 13: SendReply(1, 14, SecsEncode.L(SecsEncode.B(0), SecsEncode.L(SecsEncode.A(_cfg.Mdln), SecsEncode.A(_cfg.SoftRev))), m); return; // COMMACK=0 + MDLN/SOFTREV（Message Spec §2 E→H）
                    case 1:                                   // S1F1：记录对端版本
                        try
                        {
                            var items = m.Items();
                            var children = items.Count > 0 ? items[0].Children : null;
                            if (children is { Count: >= 2 })
                            {
                                PeerMdln = children[0].AsString();
                                PeerSoftRev = children[1].AsString();
                            }
                        }
                        catch { }
                        SendReply(1, 2, SecsEncode.L(SecsEncode.A(_cfg.Mdln), SecsEncode.A(_cfg.SoftRev)), m);
                        return;
                    case 3: HandleS1F3(m); return;
                    case 15:                                     // OFFLINE（CEID 1 状态迁移事件）
                        SendReply(1, 16, SecsEncode.B(0), m);    // OFLACK
                        SendS6F11(1, 0, null);                   // L3[U4, U2, L0]（MCS 方言）
                        return;
                    case 17:                                     // ONLINE（仅回 ONLACK；状态事件由 RESUME 后的 CEID 103 承担，现场流程）
                        SendReply(1, 18, SecsEncode.B(0), m);    // ONLACK
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
                if (m.Function == 2) { LogFrame("MCS→", $"S5F2 ACKC5={m.Items().FirstOrDefault()?.AsByte()}"); return; }
                SendS9F5(m); return;
            case 6:
                if (m.Function == 12) { LogFrame("MCS→", "S6F12 ACK"); return; }
                SendS9F5(m); return;
            case 9:
                LogFrame("MCS→", $"S9 错误报告: {Fold(m.Sml())}"); return;
            case 10:
                if (m.Function == 2) { LogFrame("MCS→", "S10F2 ACK"); return; }
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
        catch (Exception e) { Log?.Invoke("HSMS", $"S1F3 解析失败: {e.Message}", LogLevel.Warn); return; }
        SendReply(1, 4, SecsEncode.L(values.ToArray()), m);
    }

    private byte[] BuildSvidValue(ushort svid) => svid switch
    {
        14 => SecsEncode.U2(_provider.ControlState),              // ControlState（设备状态表）
        21 => SecsEncode.U2(_provider.ScState),                   // SCState（设备状态表）
        1 => BuildCarriers(),                                     // ActiveCarriers：同 15 结构
        15 => BuildCarriers(),                                    // EnhancedCarriers
        29 => SecsEncode.L(_provider.Units
                  .Take(_cfg.UnitLimit > 0 ? _cfg.UnitLimit : int.MaxValue)
                  .Select(u => SecsEncode.L(SecsEncode.A(u.UnitId), SecsEncode.U2(u.State))).ToArray()),
        3 => SecsEncode.L(_provider.Alarms.Select(a =>
                 SecsEncode.L(SecsEncode.A(a.UnitId), SecsEncode.U4(a.AlarmId), SecsEncode.A(a.AlarmText))).ToArray()),
        _ => SecsEncode.L(),
    };

    private byte[] BuildCarriers() =>
        _cfg.SvidCarrierFormat == "l2"
            ? SecsEncode.L(_provider.Carriers.Select(c =>
                  SecsEncode.L(SecsEncode.A(c.CarrierId), SecsEncode.A(c.CarrierLoc))).ToArray())
            : SecsEncode.L(_provider.Carriers.Select(c =>
                  SecsEncode.L(SecsEncode.A(c.CarrierId), SecsEncode.A(c.CarrierLoc),
                               SecsEncode.A(c.UpdatedAt), SecsEncode.U2(c.State))).ToArray());

    private void HandleS2F41(HsmsMessage m)
    {
        string rcmd = "";
        Dictionary<string, string> pars = new();
        List<SecsItem> entries = new();
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
                            entries = parsList;   // 原始条目（InventoryDataSend 的多条列表靠它传递）
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
        catch (Exception e) { Log?.Invoke("HSMS", $"S2F41 解析失败: {e.Message}", LogLevel.Warn); return; }
        Audit?.Invoke("←MCS", $"S2F41 RCMD={rcmd} 参数={string.Join(", ", pars.Select(p => $"{p.Key}={p.Value}"))}");
        OnHostCommand?.Invoke(rcmd, pars, entries);
        // 业务层通过事件回调异步处理；先按规范回 HCACK=4（确认将执行）
        byte hcack = rcmd switch
        {
            "PAUSE" or "RESUME" => 0,                             // OFFLINE→ONLINE 流程内 MCS 会发 RESUME：确认接受（现场 2026-08-15）
            "CarrierDataInstall" or "CarrierDataRemove" or "InventoryDataSend" => 4,
            _ => 1,
        };
        SendReply(2, 42, SecsEncode.L(SecsEncode.B(hcack), SecsEncode.L()), m);
        // RESUME 应答后发 SCAutoCompleted(103)；SCState 固定 2 不翻转（MCS 不发 PAUSE，现场约定）
        if (rcmd == "RESUME") SendS6F11(103, 0, null);   // L3[U4, U2, L0]（MCS 方言）
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

    private bool SendRaw(byte stream, byte func, bool wbit, byte[] body, uint sysBytes)
    {
        lock (_sendLock)
        {
            if (_stream == null) { SendFailCount++; Log?.Invoke("HSMS", $"发送失败 S{stream}F{func}（未连接）", LogLevel.Warn); return false; }
            if (sysBytes == 0) sysBytes = _sysBytes++;
            var msg = new HsmsMessage { SessionId = _sessionId, Stream = stream, Function = func, WBit = wbit, SystemBytes = sysBytes, Body = body };
            if (wbit)
                lock (_outstandingLock) _outstanding[sysBytes] = (msg.Name, DateTime.UtcNow.AddMilliseconds(_cfg.T3Ms));
            try
            {
                _stream.Write(msg.ToBytes());
                MessagesOut++;
                string hex = "", sml = "";
                if (LogFramesEnabled) { hex = Convert.ToHexString(msg.ToBytes()); sml = msg.Sml(); }
                Traffic.Add(new HsmsTrafficEntry(DateTime.Now, "发", msg.Name, sysBytes, msg.WBit, hex, sml));
                if (LogFramesEnabled)
                    Log?.Invoke("→MCS", $"{msg.Name} sys={sysBytes} HEX={hex} SML={Fold(sml)}", LogLevel.Info);
                Audit?.Invoke("→MCS", $"{msg.Name} sys={sysBytes}{(msg.WBit ? " W" : "")}");
                return true;
            }
            catch (Exception e)
            {
                SendFailCount++;
                Log?.Invoke("HSMS", $"发送异常: {e.Message}", LogLevel.Error);
                return false;
            }
        }
    }

    private async Task T3MonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct);
                var now = DateTime.UtcNow;
                var timedOut = new List<(uint Sys, string Name)>();
                lock (_outstandingLock)
                {
                    foreach (var (sys, (name, deadline)) in _outstanding.ToList())
                        if (now > deadline) { timedOut.Add((sys, name)); _outstanding.Remove(sys); }
                }
                foreach (var (sys, name) in timedOut)
                {
                    T3TimeoutCount++;
                    Log?.Invoke("HSMS", $"T3 超时: {name} sys={sys}（仅记录不发送）", LogLevel.Warn);
                    Audit?.Invoke("HSMS", $"T3 超时: {name} sys={sys}");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { Log?.Invoke("HSMS", $"T3 监控异常（监控继续）: {e.Message}", LogLevel.Error); }   // 兜底：绝不让监控任务静默死亡
        }
    }
}
