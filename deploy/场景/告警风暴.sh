#!/bin/bash
# 场景4：告警风暴 —— 16 站同码快速注入 → 每单位独立走 2.2.1 流程（102/402 各 16 次）；清除 → 101/401
# 断言：mcs 收到 102 与 101（每个单位告警独立走 2.2.1 流程，不去重）
set -e
source "$(cd "$(dirname "$0")" && pwd)/lib.sh"

echo "== 告警风暴场景"
publish_all
gen_config

# 生成 16 站 × 3 轮注入命令（同告警码 9 → 每单位独立上报；t5 起）
PLC_SCRIPT="sleep 5000\n"
for r in 1 2 3; do
  for st in $(seq 1 16); do PLC_SCRIPT+="alarm $st 9\n"; done
  PLC_SCRIPT+="sleep 300\n"
done
for st in $(seq 1 16); do PLC_SCRIPT+="alarm $st 0\n"; done
PLC_SCRIPT+="sleep 8000\nq\n"

echo "== 先起 PLC 仿真（t5 风暴注入 → mcs 验证每单位 102/402 与 101/401）"
( printf "$PLC_SCRIPT" \
    | "$REPO/publish/simulator/BufferC.Simulator.exe" plc --plc-port $PLC_PORT \
    > "$WORK/plc.log" 2>&1 ) &
PLC_PID=$!
sleep 2
start_bufferc

( printf 'sleep 9000\nwait-ceid 102 10000\nwait-ceid 101 10000\nq\n' \
    | "$REPO/publish/simulator/BufferC.Simulator.exe" mcs --mcs-port 5100 \
    > "$WORK/mcs.log" 2>&1 ) &
MCS_PID=$!
wait $PLC_PID $MCS_PID

echo "== 断言（注意：Windows 控制台重定向为 GBK，断言用 ASCII 锚点）"
OK=1
grep -q "CEID 102" "$WORK/mcs.log" && echo "    ✓ mcs 收到 CEID 102（每单位独立上报）" || { echo "    ✗ mcs 未收到 102"; OK=0; }
grep -q "CEID 101" "$WORK/mcs.log" && echo "    ✓ mcs 收到 CEID 101（每单位独立清除）" || { echo "    ✗ mcs 未收到 101"; OK=0; }

stop_all
if [ $OK -eq 1 ]; then echo "[PASS] 告警风暴场景通过 ✓"; exit 0; else echo "[FAIL] 告警风暴场景"; exit 1; fi
