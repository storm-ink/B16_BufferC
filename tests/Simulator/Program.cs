using System.Net;
using BufferC.Harness;

// 联调调试台（Windows 侧运行）：
//   BufferC.Simulator demo [--plc-port 5501] [--mcs-host 127.0.0.1] [--mcs-port 5000]   全链路自动预演（联调预演.sh 用）
//   BufferC.Simulator plc  [--plc-port 5501]                                             PLC 仿真注入台（交互/管道脚本）
//   BufferC.Simulator mcs  [--mcs-host 127.0.0.1] [--mcs-port 5000]                      MCS 仿真终端（交互/管道脚本）
// 脚本驱动：管道喂命令（# 注释 / sleep ms / EOF 退出），如: echo -e "put 1 A\nstate 1 1\nq" | Simulator plc
if (args.Length == 0)
{
    Console.WriteLine("用法:");
    Console.WriteLine("  demo: 全链路预演（联调预演.sh 调用，退出码 0=PASS）");
    Console.WriteLine("  plc : PLC 仿真注入台（put/state/alarm/avail/scan/drop/hang/echo-delay/echo-drop/reg/info/q）");
    Console.WriteLine("  mcs : MCS 仿真终端（q svid/install/remove/wait-ceid/wait-s5/info/drop/q）");
    return 1;
}
return args[0] switch
{
    "demo" => await RunDemoAsync(args),
    "plc" => await RunPlcAsync(args),
    "mcs" => await RunMcsAsync(args),
    _ => 1,
};

// ---------- demo：全链路自动预演（退出码 0 = PASS，联调预演.sh 依赖） ----------
static async Task<int> RunDemoAsync(string[] args)
{
    int plcPort = 5501, mcsPort = 5000;
    string mcsHost = "127.0.0.1";
    for (int i = 1; i < args.Length; i++)
    {
        if (i + 1 >= args.Length) break;
        switch (args[i])
        {
            case "--plc-port": plcPort = int.Parse(args[++i]); break;
            case "--mcs-host": mcsHost = args[++i]; break;
            case "--mcs-port": mcsPort = int.Parse(args[++i]); break;
        }
    }

    using var plc = new PlcSim(index: 1, unitId: 1, byteOrder: "high");
    plc.Start(IPAddress.Any, plcPort);
    Console.WriteLine($"[Plc] 仿真 PLC-1 已启动 0.0.0.0:{plc.Port}，等待 BufferC 连入（60s 超时）...");
    var deadline = Environment.TickCount64 + 60000;
    while (plc.ClientCount == 0 && Environment.TickCount64 < deadline) await Task.Delay(200);
    if (plc.ClientCount == 0)
    {
        Console.WriteLine("[FAIL] 60s 内 BufferC 未连接 PLC 仿真（检查 WSL 网关 IP / Windows 防火墙）");
        return 1;
    }
    Console.WriteLine($"[Plc] BufferC 已连入（Modbus 连接数 {plc.ClientCount}）");

    // 放入按 AGV 路径 0→2→1（状态 1 才触发 CarrierInstalled→库存更新；停在 2 只是中间态）
    plc.SetCarrierId(1, "WAFER-001");
    plc.SetStationState(1, 2);
    plc.SetCarrierId(2, "WAFER-002");
    plc.SetStationState(2, 2);
    Console.WriteLine("[Plc] 注入中间态: 站1/站2 = 2（AGV 路径 0→2→1）");
    await Task.Delay(1500);
    plc.SetStationState(1, 1);
    plc.SetStationState(2, 1);
    Console.WriteLine("[Plc] 放入完成: 站1/站2 = 1（等 poller 消化 4s）");
    await Task.Delay(4000);

    using var mcs = new McsSim();
    mcs.Connect(mcsHost, mcsPort);
    await mcs.EstablishAsync();
    Console.WriteLine("[Mcs] HSMS 建连成功（S1F13/S1F1/S1F17）");

    int carriers = await CountCarriersAsync(mcs);
    Console.WriteLine($"[Mcs] SVID15 EnhancedCarriers = {carriers}（期望 2）");
    if (carriers != 2)
    {
        Console.WriteLine("[FAIL] 货物注入后应查询到 2 个载具");
        return 1;
    }

    // 取出走手动直跳 1→0（触发 CarrierRemoved→库存移除）
    plc.SetStationState(1, 0);
    Console.WriteLine("[Plc] 站1 货物取出 1→0（等 poller 消化 4s）");
    await Task.Delay(4000);
    carriers = await CountCarriersAsync(mcs);
    Console.WriteLine($"[Mcs] SVID15 再查 = {carriers}（期望 1）");
    if (carriers != 1)
    {
        Console.WriteLine("[FAIL] 取出后应剩 1 个载具");
        return 1;
    }
    Console.WriteLine($"[Mcs] 期间收到 S6F11 事件 {mcs.ReceivedCount} 条");
    Console.WriteLine("[PASS] 联调预演通过：PLC ↔ BufferC ↔ MCS 全链路 ✓");
    return 0;

    static async Task<int> CountCarriersAsync(McsSim mcs)
    {
        var sv = await mcs.QuerySvidAsync(15);
        var carriers = sv.Count > 0 ? sv[0].Children : null;
        return carriers?.Count ?? 0;
    }
}

// ---------- plc：PLC 仿真注入台（交互 / 管道脚本） ----------
static async Task<int> RunPlcAsync(string[] args)
{
    int port = 5501;
    for (int i = 1; i < args.Length; i++)
    {
        if (i + 1 >= args.Length) break;
        if (args[i] == "--plc-port") port = int.Parse(args[++i]);
    }

    using var plc = new PlcSim(index: 1, unitId: 1, byteOrder: "high");
    plc.Start(IPAddress.Any, port);
    Console.WriteLine($"[Plc] 仿真 PLC-1 已启动 0.0.0.0:{plc.Port}（q 退出 / EOF 结束脚本）");
    Console.WriteLine("[Plc] 命令: put st id | state st v | alarm st c | avail st v | scan st code | drop | hang [on|off] | echo-delay ms | echo-drop n | reg addr [val] | info | sleep ms | q");
    while (true)
    {
        var line = Console.ReadLine();
        if (line == null) break;                                 // EOF：脚本结束
        line = line.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;  // 空行 / 注释
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0])
        {
            case "q":
            case "quit":
                return 0;
            case "sleep":
                if (parts.Length > 1 && int.TryParse(parts[1], out var ms)) await Task.Delay(ms);
                break;
            case "put":
                // AGV 放入起点：置中间态 2，再 state <st> 1 完成（状态 1 才触发库存）
                if (parts.Length >= 3 && int.TryParse(parts[1], out var sp) && sp is >= 1 and <= 16)
                {
                    plc.SetCarrierId(sp, parts[2]);
                    plc.SetStationState(sp, 2);
                    Console.WriteLine($"[Plc] 已注入 站{sp} 载具={parts[2]}（状态 2，再执行 state {sp} 1 完成放入）");
                }
                else Console.WriteLine("[Plc] 用法: put 站号 载具ID");
                break;
            case "state":
                if (parts.Length >= 3 && int.TryParse(parts[1], out var ss) && ss is >= 1 and <= 16 && ushort.TryParse(parts[2], out var sv2))
                { plc.SetStationState(ss, sv2); Console.WriteLine($"[Plc] 站{ss} 状态 = {sv2}"); }
                else Console.WriteLine("[Plc] 用法: state 站号 0|1|2|3|4");
                break;
            case "alarm":
                if (parts.Length >= 3 && int.TryParse(parts[1], out var sa) && sa is >= 1 and <= 16 && ushort.TryParse(parts[2], out var av))
                { plc.SetStationAlarm(sa, av); Console.WriteLine($"[Plc] 站{sa} 告警码 = {av}"); }
                else Console.WriteLine("[Plc] 用法: alarm 站号 码|0");
                break;
            case "avail":
                if (parts.Length >= 3 && int.TryParse(parts[1], out var sva) && sva is >= 1 and <= 16 && ushort.TryParse(parts[2], out var vv))
                { plc.SetStationAvail(sva, vv); Console.WriteLine($"[Plc] 站{sva} 可用 = {vv}（0=在线 1=下线）"); }
                else Console.WriteLine("[Plc] 用法: avail 站号 0|1");
                break;
            case "scan":
                if (parts.Length >= 3 && int.TryParse(parts[1], out var sc) && sc is >= 1 and <= 16)
                { plc.TriggerScan(sc, parts[2]); }
                else Console.WriteLine("[Plc] 用法: scan 站号 码");
                break;
            case "drop":
                plc.DropConnection();
                break;
            case "hang":
                plc.CommandHang = parts.Length < 2 || parts[1] == "on";
                Console.WriteLine($"[Plc] 命令挂起 = {(plc.CommandHang ? "ON（收到命令不回显）" : "off")}");
                break;
            case "echo-delay":
                if (parts.Length >= 2 && int.TryParse(parts[1], out var ed)) { plc.EchoDelayMs = ed; Console.WriteLine($"[Plc] 回显延迟 = {ed}ms"); }
                else Console.WriteLine("[Plc] 用法: echo-delay 毫秒");
                break;
            case "echo-drop":
                if (parts.Length >= 2 && int.TryParse(parts[1], out var dn)) { plc.EchoDropCount = dn; Console.WriteLine($"[Plc] 回显丢弃次数 = {dn}"); }
                else Console.WriteLine("[Plc] 用法: echo-drop 次数");
                break;
            case "reg":
                if (parts.Length >= 2 && int.TryParse(parts[1], out var ra) && ra is >= 0 and <= 1023)
                {
                    if (parts.Length >= 3 && ushort.TryParse(parts[2], out var rv)) { plc.SetReg(ra, rv); Console.WriteLine($"[Plc] 寄存器 {ra} = {rv}"); }
                    else Console.WriteLine($"[Plc] 寄存器 {ra} = {plc.GetReg(ra)}");
                }
                else Console.WriteLine("[Plc] 用法: reg 地址 [值]");
                break;
            case "info":
                Console.WriteLine($"[Plc] 端口={plc.Port} 客户端={plc.ClientCount} 挂起={(plc.CommandHang ? "ON" : "off")} 回显延迟={plc.EchoDelayMs}ms 回显丢弃剩余={plc.EchoDropCount}");
                break;
            default:
                Console.WriteLine($"[Plc] 未知命令: {parts[0]}");
                break;
        }
    }
    return 0;
}

// ---------- mcs：MCS 仿真终端（交互 / 管道脚本） ----------
static async Task<int> RunMcsAsync(string[] args)
{
    string host = "127.0.0.1";
    int port = 5000;
    for (int i = 1; i < args.Length; i++)
    {
        if (i + 1 >= args.Length) break;
        if (args[i] == "--mcs-host") host = args[++i];
        else if (args[i] == "--mcs-port") port = int.Parse(args[++i]);
    }

    using var mcs = new McsSim();
    mcs.Connect(host, port);
    try { await mcs.EstablishAsync(); }
    catch (Exception e) { Console.WriteLine($"[Mcs] HSMS 建连失败: {e.Message}"); return 1; }
    Console.WriteLine($"[Mcs] HSMS 已连接 {host}:{port}（S1F13/S1F1/S1F17 完成；q 退出 / EOF 结束脚本）");
    Console.WriteLine("[Mcs] 命令: q svid... | install id loc | remove id loc | wait-ceid n [t] | wait-s5 [t] | info | drop | sleep ms | q");
    while (true)
    {
        var line = Console.ReadLine();
        if (line == null) break;
        line = line.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0])
        {
            case "q":
            case "quit":
                return 0;
            case "sleep":
                if (parts.Length > 1 && int.TryParse(parts[1], out var ms)) await Task.Delay(ms);
                break;
            case "q3":
                // q3 svid... → S1F3 查询并打印（q = 退出）
                var svids = parts.Skip(1).Select(p => ushort.TryParse(p, out var u) ? u : (ushort)0).ToArray();
                if (svids.Length == 0) { Console.WriteLine("[Mcs] 用法: q 15 29"); break; }
                try
                {
                    var vals = await mcs.QuerySvidAsync(svids);
                    Console.WriteLine($"[Mcs] S1F3 → {vals.Count} 个值:");
                    foreach (var v in vals) Console.WriteLine("  " + v.ToSml(1));
                }
                catch (Exception e) { Console.WriteLine($"[Mcs] 查询失败: {e.Message}"); }
                break;
            case "install":
                if (parts.Length < 3) { Console.WriteLine("[Mcs] 用法: install 载具ID 位置(Buffer1_PortN)"); break; }
                try
                {
                    var hc = await mcs.SendS2F41Async("CarrierDataInstall", ("CARRIERID", parts[1]), ("CARRIERLOC", parts[2]));
                    Console.WriteLine($"[Mcs] CarrierDataInstall → HCACK={hc}");
                }
                catch (Exception e) { Console.WriteLine($"[Mcs] 命令失败: {e.Message}"); }
                break;
            case "remove":
                if (parts.Length < 2) { Console.WriteLine("[Mcs] 用法: remove 载具ID"); break; }
                try
                {
                    var hc = await mcs.SendS2F41Async("CarrierDataRemove", ("CARRIERID", parts[1]));
                    Console.WriteLine($"[Mcs] CarrierDataRemove → HCACK={hc}");
                }
                catch (Exception e) { Console.WriteLine($"[Mcs] 命令失败: {e.Message}"); }
                break;
            case "wait-ceid":
                if (parts.Length < 2 || !ushort.TryParse(parts[1], out var ceid)) { Console.WriteLine("[Mcs] 用法: wait-ceid CEID [超时ms]"); break; }
                {
                    int timeout = parts.Length > 2 && int.TryParse(parts[2], out var t) ? t : 5000;
                    var ev = await mcs.WaitForEventAsync(ceid, timeout);
                    Console.WriteLine(ev == null ? $"[Mcs] wait-ceid {ceid} 超时" : $"[Mcs] 收到 {ev.Name}（CEID {ceid}）");
                }
                break;
            case "wait-s5":
                {
                    int timeout = parts.Length > 1 && int.TryParse(parts[1], out var t) ? t : 5000;
                    var ev = await mcs.WaitForEventAsync(0, timeout);
                    Console.WriteLine(ev == null ? "[Mcs] wait-s5 超时" : $"[Mcs] 收到 {ev.Name}（S5F1 告警）");
                }
                break;
            case "info":
                Console.WriteLine($"[Mcs] Connected={mcs.Connected} 收到帧数={mcs.ReceivedCount}");
                break;
            case "drop":
                mcs.DropConnection();
                break;
            default:
                Console.WriteLine($"[Mcs] 未知命令: {parts[0]}");
                break;
        }
    }
    return 0;
}
