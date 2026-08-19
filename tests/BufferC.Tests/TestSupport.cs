using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BufferC.Tests;

/// <summary>
/// AGVC 假服务器（HttpListener）：记录请求路径+body；应答 queryMachines 规格
/// {code, message, reqCode, interrupt, data:[{carrierId,...}]}。
/// 注入：前 FailCount 次应答 code="1"（无 data）；QueryCode=正常 code；QueryCarrierId=null 时 data 空数组。
/// FreePort()：探测空闲端口（另用于模拟"服务器不可达"）。
/// </summary>
internal sealed class FakeAgvcServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<(string Path, string Body)> _requests = new();
    private readonly object _lock = new();
    private volatile int _failCount;

    public volatile string QueryCode = "0";          // 正常应答的 code 字段
    public volatile string? QueryCarrierId;          // null → data 空数组
    public int Port { get; private set; }
    public int FailCount { set => _failCount = value; }
    public IReadOnlyList<(string Path, string Body)> Requests { get { lock (_lock) return _requests.ToList(); } }

    public FakeAgvcServer()
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }                                 // Stop() 时退出
            _ = Task.Run(async () =>
            {
                string body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                lock (_lock) _requests.Add((ctx.Request.Url!.AbsolutePath, body));
                // FailCount 只作用于 queryMachines（推送请求不消耗失败计数，保证重试断言确定性）
                bool fail = _failCount > 0 && ctx.Request.Url!.AbsolutePath == "/mpms/api/hsmsRpc/queryMachines";
                if (fail) Interlocked.Decrement(ref _failCount);
                string code = fail ? "1" : QueryCode;
                string resp;
                if (ctx.Request.Url!.AbsolutePath == "/mpms/api/hsmsRpc/pushDeviceStatusInfo")
                    resp = $"{{\"code\":\"{code}\",\"data\":\"\",\"interrupt\":false,\"message\":\"{(fail ? "busy" : "ok")}\",\"reqCode\":\"REQ001\"}}";
                else
                {
                    string data = (fail || QueryCarrierId == null) ? "[]" : $"[{{\"carrierId\":\"{QueryCarrierId}\"}}]";
                    resp = $"{{\"code\":\"{code}\",\"message\":\"{(fail ? "busy" : "ok")}\",\"reqCode\":\"REQ001\",\"interrupt\":false,\"data\":{data}}}";
                }
                var buf = Encoding.UTF8.GetBytes(resp);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = buf.Length;
                await ctx.Response.OutputStream.WriteAsync(buf, ct);
                ctx.Response.OutputStream.Close();
            }, ct);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
    }
}
