#!/bin/bash
# 场景1：PLC 断线重连 —— 注入货物 → 断连 → 重连后状态同步 → 取出
# 断言：日志出现 "连接/轮询异常" 后再次 "已连接"；mcs 收到 204（放入）与 203（取出）
set -e
source "$(cd "$(dirname "$0")" && pwd)/lib.sh"

echo "== 断线重连场景"
publish_all
gen_config

echo "== 先起 PLC 仿真（BufferC 连入后注入：t5 放入 → t7.5 断连 → 重连 → t13.5 取出）"
( printf 'sleep 5000\nput 1 WAFER-101\nstate 1 1\nsleep 2500\ndrop\nsleep 6000\nstate 1 0\nsleep 8000\nq\n' \
    | "$REPO/publish/simulator/BufferC.Simulator.exe" plc --plc-port $PLC_PORT \
    > "$WORK/plc.log" 2>&1 ) &
PLC_PID=$!
sleep 2
start_bufferc

( printf 'sleep 9000\nwait-ceid 204 10000\nwait-ceid 203 10000\nq\n' \
    | "$REPO/publish/simulator/BufferC.Simulator.exe" mcs --mcs-port 5100 \
    > "$WORK/mcs.log" 2>&1 ) &
MCS_PID=$!
wait $PLC_PID $MCS_PID

echo "== 断言（注意：Windows 控制台重定向为 GBK，断言用 ASCII 锚点）"
OK=1
grep -q "Unable to read data from the transport connection" "$WORK/scene-run.log" && echo "    ✓ 日志含: 连接异常（断线）" || { echo "    ✗ 日志缺: 连接异常"; OK=0; }
[ "$(grep -c '127.0.0.1:5510' "$WORK/scene-run.log")" -ge 2 ] && echo "    ✓ 日志含: 连接成功 ×≥2（断线重连成功）" || { echo "    ✗ 重连后无再次 连接成功"; OK=0; }
grep -q "CEID 204" "$WORK/mcs.log" && echo "    ✓ mcs 收到 CEID 204（放入）" || { echo "    ✗ mcs 未收到 204"; OK=0; }
grep -q "CEID 203" "$WORK/mcs.log" && echo "    ✓ mcs 收到 CEID 203（取出）" || { echo "    ✗ mcs 未收到 203"; OK=0; }

stop_all
if [ $OK -eq 1 ]; then echo "[PASS] 断线重连场景通过 ✓"; exit 0; else echo "[FAIL] 断线重连场景"; exit 1; fi
