#!/bin/bash
# 制作现场 linux 包：发布 → 覆盖生产模板 config.json → 打 tgz（全自动，无人工改配置环节）
# 为什么自动化：08-14 与 08-17 各踩一次「发布后忘把 agvc.baseUrl 改回空」——
#   联调地址（127.0.0.1:5502）随包到现场会向不存在的地址发 AGVC 请求；人工规程靠不住，写死进脚本。
# 用法: bash deploy/制作现场linux包.sh
# 产物: 仓库根 bufferc-linux-x64.tgz（现场包：B01/B02 现场真实地址 192.168.100.182/178:5000、
#   B03~B09 占位 192.168.1.12~18 待接入（B03=16 站其余 8 站）、agvc 现场地址 192.168.100.23:8282、
#   webPort 7001、logLevel debug（联调口径，投产改 info））
set -e

REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO"

echo "== [1/3] 发布 linux-x64（自包含）"
dotnet publish src/BufferC.Host -r linux-x64 --self-contained -c Release -o publish/linux-x64

echo "== [2/3] 覆盖现场模板 config.json（B01/B02 真实地址 / 其余占位 / webPort 7001 / logLevel debug）"
# 纯 bash 写 JSON（不依赖 python——Windows 上 python 可能是商店存根）
# 现场布局（2026-08-20）：9 台 MAGV03B01~09；B01/B02 已接入（192.168.100.182/178:5000，8 站），
# B03~B09 占位 192.168.1.12~18 待接入（B03=16 站，其余 8 站）
cat > publish/linux-x64/config.json <<'JSONEOF'
{
  "plcs": [
    { "index": 1, "ip": "192.168.100.182", "port": 5000, "unitId": 1, "byteOrder": "low", "timeoutMs": 3000, "lastSeq": 0, "name": "MAGV03B01", "stations": 8 },
    { "index": 2, "ip": "192.168.100.178", "port": 5000, "unitId": 1, "byteOrder": "low", "timeoutMs": 3000, "lastSeq": 0, "name": "MAGV03B02", "stations": 8 },
    { "index": 3, "ip": "192.168.1.12", "port": 502, "unitId": 1, "byteOrder": "low", "timeoutMs": 3000, "lastSeq": 0, "name": "MAGV03B03", "stations": 16 },
    { "index": 4, "ip": "192.168.1.13", "port": 502, "unitId": 1, "byteOrder": "low", "timeoutMs": 3000, "lastSeq": 0, "name": "MAGV03B04", "stations": 8 },
    { "index": 5, "ip": "192.168.1.14", "port": 502, "unitId": 1, "byteOrder": "low", "timeoutMs": 3000, "lastSeq": 0, "name": "MAGV03B05", "stations": 8 },
    { "index": 6, "ip": "192.168.1.15", "port": 502, "unitId": 1, "byteOrder": "low", "timeoutMs": 3000, "lastSeq": 0, "name": "MAGV03B06", "stations": 8 },
    { "index": 7, "ip": "192.168.1.16", "port": 502, "unitId": 1, "byteOrder": "low", "timeoutMs": 3000, "lastSeq": 0, "name": "MAGV03B07", "stations": 8 },
    { "index": 8, "ip": "192.168.1.17", "port": 502, "unitId": 1, "byteOrder": "low", "timeoutMs": 3000, "lastSeq": 0, "name": "MAGV03B08", "stations": 8 },
    { "index": 9, "ip": "192.168.1.18", "port": 502, "unitId": 1, "byteOrder": "low", "timeoutMs": 3000, "lastSeq": 0, "name": "MAGV03B09", "stations": 8 }
  ],
  "hsms": { "listenPort": 5000, "mdln": "BUFFERC", "softRev": "0.1.0", "t3Ms": 45000 },
  "agvc": { "baseUrl": "http://192.168.100.23:8282", "timeoutSec": 5, "retryCount": 3, "retryIntervalMs": 3000,
            "cmsIndexBase": 10000, "arrivalGraceMs": 3000 },
  "pollIntervalMs": 500,
  "echoTimeoutMs": 5000,
  "echoRetryCount": 1,
  "echoPollIntervalMs": 100,
  "reconnectMaxBackoffMs": 30000,
  "historyRetentionRows": 2000,
  "debugReadChunkWords": 16,
  "cmdWriteChunkWords": 16,
  "logFile": "bufferc.log",
  "logLevel": "debug",
  "logRetentionDays": 7,
  "auditFile": "bufferc.audit.log",
  "reconcileStrict": false,
  "dbPath": "bufferc.db",
  "webPort": 7001
}
JSONEOF
grep -n '"baseUrl"\|"logLevel"\|"webPort"\|192\.168\.' publish/linux-x64/config.json | sed 's/^/  /'

echo "== [3/3] 打 tgz"
rm -f bufferc-linux-x64.tgz
tar czf bufferc-linux-x64.tgz -C publish/linux-x64 --exclude='*.pdb' .
ls -lh bufferc-linux-x64.tgz | sed 's/^/  /'

echo
echo "== 完成。服务器上部署前改 config.json 的现场项："
echo "   plcs[].ip / unitId / byteOrder（真实 PLC）；agvc.baseUrl（现场 AGVC 地址，不用则留空）"
echo "   部署步骤见 deploy/部署说明.md"
