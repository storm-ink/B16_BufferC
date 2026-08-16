using System.Collections.Concurrent;
using System.Net.Sockets;
using BufferC.Core.Secs;

namespace BufferC.Harness;

/// <summary>
/// MCS 仿真器（HSMS Active 客户端）：建连（S1F13/1/17）→ 查询 S1F3 → 命令 S2F41 →
/// 接收并校验 S6F11/S5F1 事件。按场景脚本驱动。
/// </summary>
public sealed class McsSim : IDisposable
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private uint _sysBytes = 1000;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<HsmsMessage>> _pending = new();
    private readonly ConcurrentQueue<HsmsMessage> _received = new();
    private CancellationTokenSource _cts = new();

    public int ReceivedCount => _received.Count;
    public bool Connected => _tcp?.Connected == true;

    public void Connect(string host, int port)
    {
        _tcp = new TcpClient();
        _tcp.Connect(host, port);
        _stream = _tcp.GetStream();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    /// <summary>断连注入（联调调试台）：掐断连接并清空收/发状态；之后可重新 Connect + EstablishAsync</summary>
    public void DropConnection()
    {
        Console.WriteLine("[McsSim] 注入: 断连");
        _cts.Cancel();
        _tcp?.Dispose();
        _stream = null;
        _pending.Clear();
        _received.Clear();
    }

    /// <summary>建连：S1F13 → S1F14、S1F1 → S1F2、S1F17 → S1F18（Ameth 初始化流程）</summary>
    public async Task EstablishAsync()
    {
        var r14 = await PrimaryAsync(1, 13, SecsEncode.L());
        if (r14 == null || r14.Function != 14) throw new Exception("S1F14 未收到");
        var r2 = await PrimaryAsync(1, 1, SecsEncode.L(SecsEncode.A("MCS-SIM"), SecsEncode.A("1.0")));
        if (r2 == null) throw new Exception("S1F2 未收到");
        var r18 = await PrimaryAsync(1, 17, Array.Empty<byte>());
        if (r18 == null) throw new Exception("S1F18 未收到");
    }

    /// <summary>发送主消息并等待回复</summary>
    public async Task<HsmsMessage?> PrimaryAsync(byte stream, byte func, byte[] body, int timeoutMs = 5000)
    {
        uint sys = _sysBytes++;
        var tcs = new TaskCompletionSource<HsmsMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[sys] = tcs;
        var msg = new HsmsMessage { Stream = stream, Function = func, WBit = true, SystemBytes = sys, Body = body };
        _stream!.Write(msg.ToBytes());
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        _pending.TryRemove(sys, out _);
        return done == tcs.Task ? await tcs.Task : null;
    }

    /// <summary>发 S2F41，返回 HCACK（timeoutMs：CI 高负载下放宽）</summary>
    public async Task<byte> SendS2F41Async(string rcmd, params (string Name, string Value)[] pars)
        => await SendS2F41Async(15000, rcmd, pars);

    public async Task<byte> SendS2F41Async(int timeoutMs, string rcmd, params (string Name, string Value)[] pars)
    {
        var items = pars.Select(p => SecsEncode.L(SecsEncode.A(p.Name), SecsEncode.A(p.Value))).ToArray();
        var body = SecsEncode.L(SecsEncode.A(rcmd), SecsEncode.L(items));
        var r = await PrimaryAsync(2, 41, body, timeoutMs);
        if (r == null || r.Function != 42) throw new Exception("S2F42 未收到");
        var it = r.Items();
        if (it.Count == 0) return 255;
        var hc = it[0].Children;
        return hc is { Count: > 0 } ? hc[0].AsByte() : (byte)255;
    }

    /// <summary>S1F3 查询，返回 S1F4 的值列表（L 包装内的子项）</summary>
    public async Task<List<SecsItem>> QuerySvidAsync(params ushort[] svids)
    {
        var r = await PrimaryAsync(1, 3, SecsEncode.L(svids.Select(SecsEncode.U2).ToArray()));
        if (r == null || r.Function != 4) throw new Exception("S1F4 未收到");
        var items = r.Items();
        var values = items.Count > 0 ? items[0].Children : null;
        return values ?? new List<SecsItem>();
    }

    /// <summary>扫描式匹配：只取走匹配项，不匹配的放回队列（避免顺序性丢事件）</summary>
    private bool Matches(HsmsMessage m, ushort ceid)
    {
        if (m.Stream == 6 && m.Function == 11)
        {
            // S6F11 body = L3[U4 dataid, U2 ceid, L1[...]] —— CEID 在 L 包装内
            var items = m.Items();
            var children = items.Count > 0 ? items[0].Children : null;
            return children is { Count: > 1 } && children[1].AsUInt() == ceid;
        }
        return ceid == 0 && m.Stream == 5 && m.Function == 1;   // 任意 S5F1
    }

    private static void HoldBack(ConcurrentQueue<HsmsMessage> q, List<HsmsMessage> hold)
    {
        foreach (var h in hold) q.Enqueue(h);
    }

    /// <summary>统计指定 CEID 的 S6F11 数量（达到 expected 提前返回；用于去重断言）</summary>
    public async Task<int> WaitForEventCountAsync(ushort ceid, int expected, int timeoutMs = 3000)
    {
        int count = 0;
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var hold = new List<HsmsMessage>();
            while (_received.TryDequeue(out var m))
            {
                if (Matches(m, ceid)) { count++; if (count >= expected) { HoldBack(_received, hold); return count; } }
                else hold.Add(m);
            }
            HoldBack(_received, hold);
            await Task.Delay(50);
        }
        return count;
    }

    /// <summary>等待设备上报指定 CEID 的 S6F11（ceid=0 时等任意 S5F1；只取匹配项，其余保留）</summary>
    public async Task<HsmsMessage?> WaitForEventAsync(ushort ceid, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var hold = new List<HsmsMessage>();
            HsmsMessage? found = null;
            while (_received.TryDequeue(out var m))
            {
                if (found == null && Matches(m, ceid)) { found = m; continue; }
                hold.Add(m);
            }
            HoldBack(_received, hold);
            if (found != null) return found;
            await Task.Delay(50);
        }
        return null;
    }

    // ---------- 接收循环 ----------
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var lenBuf = await ReadExactlyAsync(_stream!, 4, ct);
                if (lenBuf == null) break;
                int len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
                if (len < 10) break;
                var frame = await ReadExactlyAsync(_stream!, len, ct);
                if (frame == null) break;
                var m = HsmsMessage.Parse(frame);
                if (m.WBit)
                {
                    _received.Enqueue(m);
                    // 设备主动上报 → 回 ACK（S6F12 / S5F2）
                    if (m.Stream == 6 && m.Function == 11)
                        SendRaw(6, 12, false, SecsEncode.B(0), m.SystemBytes);
                    else if (m.Stream == 5 && m.Function == 1)
                        SendRaw(5, 2, false, SecsEncode.B(0), m.SystemBytes);
                }
                else if (_pending.TryRemove(m.SystemBytes, out var tcs))
                {
                    tcs.TrySetResult(m);
                }
            }
        }
        catch { /* 断连 */ }
    }

    private void SendRaw(byte stream, byte func, bool wbit, byte[] body, uint sysBytes)
    {
        try
        {
            _stream!.Write(new HsmsMessage { Stream = stream, Function = func, WBit = wbit, SystemBytes = sysBytes, Body = body }.ToBytes());
        }
        catch { /* 断连 */ }
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

    public void Dispose()
    {
        _cts.Cancel();
        _tcp?.Dispose();
    }
}
