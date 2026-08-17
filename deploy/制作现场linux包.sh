#!/bin/bash
# 制作现场 linux 包：发布 → 覆盖生产模板 config.json → 打 tgz（全自动，无人工改配置环节）
# 为什么自动化：08-14 与 08-17 各踩一次「发布后忘把 agvc.baseUrl 改回空」——
#   联调地址（127.0.0.1:5502）随包到现场会向不存在的地址发 AGVC 请求；人工规程靠不住，写死进脚本。
# 用法: bash deploy/制作现场linux包.sh
# 产物: 仓库根 bufferc-linux-x64.tgz（现场包：15 台 PLC 占位 192.168.1.10~24、baseUrl 空、webPort 7001、logLevel info）
set -e

REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO"

echo "== [1/3] 发布 linux-x64（自包含）"
dotnet publish src/BufferC.Host -r linux-x64 --self-contained -c Release -o publish/linux-x64

echo "== [2/3] 覆盖现场模板 config.json（baseUrl 空 / 15 台 PLC 占位 / webPort 7001 / logLevel info）"
python - <<'PYEOF'
import json
plcs = [{"index": i + 1, "ip": f"192.168.1.{10 + i}", "port": 502, "unitId": 1,
         "byteOrder": "high", "timeoutMs": 3000, "lastSeq": 0} for i in range(15)]
cfg = {
    "plcs": plcs,
    "hsms": {"listenPort": 5000, "mdln": "BUFFERC", "softRev": "0.1.0", "t3Ms": 45000},
    "agvc": {"baseUrl": "", "timeoutSec": 5, "retryCount": 3, "retryIntervalMs": 2000,
             "cmsIndexBase": 10000, "arrivalGraceMs": 3000},
    "pollIntervalMs": 500,
    "echoTimeoutMs": 5000,
    "echoRetryCount": 1,
    "echoPollIntervalMs": 100,
    "reconnectMaxBackoffMs": 30000,
    "historyRetentionRows": 2000,
    "debugReadChunkWords": 16,
    "logFile": "bufferc.log",
    "logLevel": "info",
    "logRetentionDays": 7,
    "auditFile": "bufferc.audit.log",
    "reconcileStrict": False,
    "dbPath": "bufferc.db",
    "webPort": 7001,
}
json.dump(cfg, open("publish/linux-x64/config.json", "w"), indent=2, ensure_ascii=False)
PYEOF
grep -n '"baseUrl"\|"logLevel"\|"webPort"\|192.168.1.1[09]' publish/linux-x64/config.json | sed 's/^/  /'

echo "== [3/3] 打 tgz"
rm -f bufferc-linux-x64.tgz
tar czf bufferc-linux-x64.tgz -C publish/linux-x64 --exclude='*.pdb' .
ls -lh bufferc-linux-x64.tgz | sed 's/^/  /'

echo
echo "== 完成。服务器上部署前改 config.json 的现场项："
echo "   plcs[].ip / unitId / byteOrder（真实 PLC）；agvc.baseUrl（现场 AGVC 地址，不用则留空）"
echo "   部署步骤见 deploy/部署说明.md"
