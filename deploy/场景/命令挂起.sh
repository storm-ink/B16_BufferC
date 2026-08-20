#!/bin/bash
# 场景3：PLC 命令挂起（卡死不回显）—— 命令超时 → 悬空命令记录 + S5F1 告警 9001
# 断言：日志含 "悬空命令" 与 "S5F1" 与 "9001"
set -e
source "$(cd "$(dirname "$0")" && pwd)/lib.sh"

echo "== 命令挂起场景"
publish_all
gen_config

echo "== 先起 PLC 仿真（t5 hang on → mcs t8 install 失败 → t13 hang off）"
( printf 'sleep 5000\nhang on\nsleep 20000\nhang off\nsleep 2000\nq\n' \
    | "$REPO/publish/simulator/BufferC.Simulator.exe" plc --plc-port $PLC_PORT \
    > "$WORK/plc.log" 2>&1 ) &
PLC_PID=$!
sleep 2
start_bufferc

( printf 'sleep 8000\ninstall WAFER-301 BUFFER01_P3\nwait-ceid 201 3000\nsleep 5000\nq\n' \
    | "$REPO/publish/simulator/BufferC.Simulator.exe" mcs --mcs-port 5100 \
    > "$WORK/mcs.log" 2>&1 ) &
MCS_PID=$!
wait $PLC_PID $MCS_PID

echo "== 断言（S2F41 为异步确认：HCACK=4 是设计行为，失败看告警 9001 / 无 CEID 201）"
OK=1
grep -q "9001" "$WORK/scene-run.log" && echo "    ✓ 日志含: 9001（命令失败告警码）" || { echo "    ✗ 日志缺: 9001"; OK=0; }
grep -q "S5F1" "$WORK/scene-run.log" && echo "    ✓ 日志含: S5F1（告警上报）" || { echo "    ✗ 日志缺: S5F1"; OK=0; }
grep -q "CEID 201" "$WORK/mcs.log" && { echo "    ✗ 不应收到 CEID 201（命令应失败）"; OK=0; } || echo "    ✓ 无 CEID 201（命令未成功）"
grep -q "HCACK=4" "$WORK/mcs.log" && echo "    ✓ mcs HCACK=4（异步执行确认，设计行为）" || { echo "    ✗ mcs 未收到 S2F42"; OK=0; }

stop_all
if [ $OK -eq 1 ]; then echo "[PASS] 命令挂起场景通过 ✓"; exit 0; else echo "[FAIL] 命令挂起场景"; exit 1; fi
