#!/usr/bin/env bash
# MCS 联调（无真 PLC）：Simulator plc 假 PLC + BufferC.Host + 前端「MCS 联调」页
# 用法：bash deploy/mcs联调启动.sh   （浏览器开 http://localhost:7001）
set -e
cd "$(dirname "$0")/.."

HOST="publish/win-x64/BufferC.Host.exe"
SIM="publish/simulator/BufferC.Simulator.exe"
[ -f "$HOST" ] || { echo "缺少 $HOST —— 先执行：dotnet publish src/BufferC.Host -r win-x64 --self-contained -c Release -o publish/win-x64"; exit 1; }
[ -f "$SIM" ] || { echo "缺少 $SIM —— 先发布 Simulator"; exit 1; }

"$SIM" plc --plc-port 5501 &
SIM_PID=$!
trap 'kill $SIM_PID 2>/dev/null' EXIT
sleep 1

echo "== 假 PLC 已启动 127.0.0.1:5501（命令回显/扫码握手全支持） =="
echo "== MCS 远程连入前，管理员 PowerShell 放行 5000（一次即可）： =="
echo '   netsh advfirewall firewall add rule name="BufferC-HSMS" dir=in action=allow protocol=TCP localport=5000'
echo "== 浏览器打开 http://localhost:7001 →「MCS 联调」页签 =="
echo "== 把本机 IP:5000 告知 MCS 侧；Ctrl+C 停止（假 PLC 一并退出） =="
echo
"$HOST" config-mcs-test.json
