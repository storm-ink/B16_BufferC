#!/bin/bash
# 故障注入场景公共库（Git Bash 侧 source）：发布/启动/等待/清理/断言
REPO="C:/Users/Sineva_CL/Desktop/B16_BufferC"
SCENE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$REPO/publish/scene"
PLC_PORT=5510

# 发布：每次重发（win-x64 自包含 + Simulator）
# 注意（本机验证踩坑）：① 发布前必须停掉运行中的 BufferC（Windows 文件锁会静默导致 exe 覆盖失败、继续跑旧代码）
# ② 必须清空旧目录再发（历史 net10 产物残留会混入 net6 发布，WebUi 加载时崩溃）
publish_all() {
  stop_all
  rm -rf "$REPO/publish/win-x64" "$REPO/publish/simulator"
  dotnet publish "$REPO/src/BufferC.Host" -c Release -o "$REPO/publish/win-x64" --self-contained >/dev/null 2>&1
  dotnet publish "$REPO/tests/Simulator" -c Release -o "$REPO/publish/simulator" >/dev/null 2>&1
}

# 生成单 PLC 场景 config（hsms 5000 / plc 127.0.0.1:$PLC_PORT / webPort 0）
gen_config() {
  mkdir -p "$WORK"
  python - "$PLC_PORT" "$WORK" <<'PYEOF'
import json, sys
port, work = int(sys.argv[1]), sys.argv[2]
cfg = {
    "plcs": [{"index": 1, "ip": "127.0.0.1", "port": port, "unitId": 1, "byteOrder": "high", "timeoutMs": 3000, "lastSeq": 0}],
    "hsms": {"listenPort": 5000, "mdln": "BUFFERC", "softRev": "0.1.0", "t3Ms": 45000},
    "pollIntervalMs": 200, "echoTimeoutMs": 1000, "echoRetryCount": 1,
    "logFile": "scene.log", "webPort": 0,
}
json.dump(cfg, open(work + "/config.json", "w"), indent=2)
PYEOF
}

# 启动 BufferC（后台）并等 PLC 已连接（30s 超时）
# 注意：Windows 控制台重定向输出为 GBK，grep 中文会乱码失配——用 ASCII 的 "ip:port" 作锚点
start_bufferc() {
  ( cd "$WORK" && "$REPO/publish/win-x64/BufferC.Host.exe" config.json > "$WORK/scene-run.log" 2>&1 ) &
  BUFFERC_PID=$!
  local anchor="127.0.0.1:$PLC_PORT"
  local deadline=$(( $(date +%s) + 30 ))
  while ! grep -q "$anchor" "$WORK/scene-run.log" 2>/dev/null && [ $(date +%s) -lt $deadline ]; do sleep 1; done
  if ! grep -q "$anchor" "$WORK/scene-run.log" 2>/dev/null; then
    echo "[FAIL] BufferC 30s 内未连接 PLC 仿真"; tail -5 "$WORK/scene-run.log"; stop_all; exit 1
  fi
  echo "    BufferC 已启动并连上 PLC"
}

stop_all() {
  taskkill //F //IM BufferC.Host.exe >/dev/null 2>&1 || true
}

# 断言：日志包含全部关键字（grep 计数 ≥1）
assert_log() {
  local ok=1
  for kw in "$@"; do
    if grep -q "$kw" "$WORK/scene-run.log"; then echo "    ✓ 日志含: $kw"; else echo "    ✗ 日志缺: $kw"; ok=0; fi
  done
  [ $ok -eq 1 ]
}
