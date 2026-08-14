#!/bin/bash
# BufferC 离线一键安装（服务器无外网场景）
# 用法: sudo bash install.sh        # 需与 bufferc-linux-x64.tgz、bufferc.service 同目录
# 演练: BUFFERC_DIR=~/offline-test bash install.sh   # 覆盖安装目录（不写 systemd）
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
TARGET="${BUFFERC_DIR:-/opt/bufferc}"

if [ ! -f "$SCRIPT_DIR/bufferc-linux-x64.tgz" ]; then
  echo "错误: 未找到 bufferc-linux-x64.tgz（请与 install.sh 放同一目录）"
  exit 1
fi

echo "==> 解包到 $TARGET"
mkdir -p "$TARGET"
tar xzf "$SCRIPT_DIR/bufferc-linux-x64.tgz" -C "$TARGET"
# Windows 侧打的 tar 可能不带 +x（踩过：Permission denied）
chmod +x "$TARGET/BufferC.Host"

if [ -n "${BUFFERC_DIR:-}" ]; then
  echo "==> 演练模式（BUFFERC_DIR 已设置），跳过 systemd 安装"
  echo "==> 产物: $TARGET"
  echo "==> 手动启动验证: cd $TARGET && ./BufferC.Host config.json"
  exit 0
fi

echo "==> 安装 systemd 服务"
cp "$SCRIPT_DIR/bufferc.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now bufferc

sleep 2
echo "==> 服务状态"
systemctl status bufferc --no-pager | head -12 || true

echo
echo "==> 已完成。下一步（现场联调）："
echo "    sudo nano $TARGET/config.json   # 填 15 台 PLC 真实 IP/unitId/byteOrder"
echo "    sudo systemctl restart bufferc"
echo "    Web 界面: http://<服务器IP>:8080"
echo "    防火墙(如需, CentOS 7 firewalld): sudo firewall-cmd --permanent --add-port=5000/tcp --add-port=8080/tcp && sudo firewall-cmd --reload"
