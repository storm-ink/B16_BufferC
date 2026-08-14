#!/bin/bash
# 联调预演：Windows 侧仿真（PlcSim + McsSim）+ CentOS7 WSL 侧 BufferC（模拟现场部署形态）
# 用法: bash 联调预演.sh        （前置：CentOS7 WSL 已导入；bufferc-linux-x64.tgz 已更新）
set -e

DEMO_PORT=5501
REPO="C:/Users/Sineva_CL/Desktop/B16_BufferC"

echo "== [1/6] 发布 Simulator（Windows 侧仿真器）+ 同步现场手册"
dotnet publish "$REPO/tests/Simulator" -c Release -o "$REPO/publish/simulator" 2>&1 | tail -1
# 现场使用手册规范副本在 deploy/（publish/ 被 gitignore，直接改会丢）——每次预演同步到分发包
mkdir -p "$REPO/publish/win-x64"
cp "$REPO/deploy/现场使用手册.md" "$REPO/publish/win-x64/现场使用手册.md"

echo "== [2/6] 取 WSL 网关 IP（BufferC 将经它连 Windows 的 PLC 仿真）"
ROUTE=$(wsl -d CentOS7 -- cat /proc/net/route | tr -d '\r')
GW=$(echo "$ROUTE" | python -c "
import sys
for line in sys.stdin:
    f = line.split()
    if len(f) >= 3 and f[1] == '00000000':
        h = f[2]
        print(f'{int(h[6:8],16)}.{int(h[4:6],16)}.{int(h[2:4],16)}.{int(h[0:2],16)}')
        break
")
if [ -z "$GW" ]; then echo "错误: 无法获取 WSL 网关 IP"; exit 1; fi
echo "    网关 IP: $GW"

echo "== [3/6] 生成 demo config.json（PLC-1 → Windows 仿真器）"
python - "$GW" "$DEMO_PORT" "$REPO" <<'PYEOF'
import json, sys
gw, port, repo = sys.argv[1], int(sys.argv[2]), sys.argv[3]
cfg = {
    "plcs": [{"index": 1, "ip": gw, "port": port, "unitId": 1, "byteOrder": "high", "timeoutMs": 3000, "lastSeq": 0}],
    "hsms": {"listenPort": 5000, "mdln": "BUFFERC", "softRev": "0.1.0", "t3Ms": 45000},
    "pollIntervalMs": 500, "echoTimeoutMs": 5000, "echoRetryCount": 1,
    "logFile": "demo.log", "dbPath": "demo.db", "webPort": 8080,
}
json.dump(cfg, open(repo + "/publish/simulator/demo-config.json", "w"), indent=2)
PYEOF

echo "== [4/6] 更新 WSL 侧产物 + 拷入 demo config"
wsl -d CentOS7 -- bash -c "rm -rf ~/bufferc ~/demo && mkdir -p ~/bufferc ~/demo && \
  tar xzf /mnt/c/Users/Sineva_CL/Desktop/B16_BufferC/bufferc-linux-x64.tgz -C ~/bufferc && chmod +x ~/bufferc/BufferC.Host && \
  cp /mnt/c/Users/Sineva_CL/Desktop/B16_BufferC/publish/simulator/demo-config.json ~/demo/config.json"

echo "== [5/6] 启动：Simulator（Windows）+ BufferC（WSL CentOS7）"
# 注意：wsl.exe 本体放 Windows 后台（会话保持活跃，BufferC 前台跑在会话里）——
# 若在 WSL 内部 nohup 后台化，bash 退出后实例关闭会连带杀掉 BufferC（踩过）
"$REPO/publish/simulator/BufferC.Simulator.exe" demo --plc-port $DEMO_PORT --mcs-host 127.0.0.1 --mcs-port 5000 \
  > "$REPO/publish/simulator/sim.log" 2>&1 &
SIM_PID=$!
sleep 1
wsl -d CentOS7 -- bash -c "cd ~/demo && exec ~/bufferc/BufferC.Host config.json" \
  > "$REPO/publish/simulator/bufferc-wsl.log" 2>&1 &
WSL_PID=$!

echo "== [6/6] 等待 Simulator 完成（自动注入/查询验证）"
SIM_EXIT=0
wait $SIM_PID || SIM_EXIT=$?
cat "$REPO/publish/simulator/sim.log"

echo "---- BufferC(WSL) 日志尾部 ----"
tail -8 "$REPO/publish/simulator/bufferc-wsl.log"
echo "---- 清理 ----"
kill $WSL_PID 2>/dev/null
wsl -d CentOS7 -- bash -c "pkill -f BufferC.Host" 2>/dev/null
echo "---- Simulator 退出码: $SIM_EXIT（0 = PASS）----"
exit $SIM_EXIT
