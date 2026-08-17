#!/bin/bash
# 场景2：命令回显丢失 —— PLC 执行但丢弃回显 1 次 → 命令通道重试后成功（S6F11 201）
# 断言：日志含 "重试" 与 "S6F11 201"
set -e
source "$(cd "$(dirname "$0")" && pwd)/lib.sh"

echo "== 回显丢失场景"
publish_all
gen_config

echo "== 先起 PLC 仿真（t5 设 echo-drop 1 → mcs t8 install 应重试后成功）"
( printf 'sleep 5000\necho-drop 1\nsleep 20000\nq\n' \
    | "$REPO/publish/simulator/BufferC.Simulator.exe" plc --plc-port $PLC_PORT \
    > "$WORK/plc.log" 2>&1 ) &
PLC_PID=$!
sleep 2
start_bufferc

( printf 'sleep 8000\ninstall WAFER-201 Buffer1_Port2\nwait-ceid 201 8000\nq\n' \
    | "$REPO/publish/simulator/BufferC.Simulator.exe" mcs --mcs-port 5100 \
    > "$WORK/mcs.log" 2>&1 ) &
MCS_PID=$!
wait $PLC_PID $MCS_PID

echo "== 断言（S2F41 为异步确认：HCACK=4 是设计行为，成败看 CEID 201 事件 / 告警 9001）"
OK=1
grep -q "\[Cmd\]" "$WORK/scene-run.log" && echo "    ✓ 日志含: [Cmd] 重试标记" || { echo "    ✗ 日志缺: [Cmd]（命令未重试？）"; OK=0; }
grep -q "CEID 201" "$WORK/mcs.log" && echo "    ✓ mcs 收到 CEID 201（重试后安装成功）" || { echo "    ✗ mcs 未收到 CEID 201"; OK=0; }
grep -q "9001" "$WORK/scene-run.log" && { echo "    ✗ 不应出现告警 9001（命令已成功）"; OK=0; } || echo "    ✓ 无告警 9001"
grep -q "HCACK=4" "$WORK/mcs.log" && echo "    ✓ mcs HCACK=4（异步执行确认，设计行为）" || { echo "    ✗ mcs 未收到 S2F42"; OK=0; }

stop_all
if [ $OK -eq 1 ]; then echo "[PASS] 回显丢失场景通过 ✓"; exit 0; else echo "[FAIL] 回显丢失场景"; exit 1; fi
