#!/bin/bash
# 制作离线开发包：把源码 + NuGet 缓存 + 安装说明整理成 U 盘目录结构
# 用法: bash deploy/制作离线开发包.sh [输出目录] [--min-nuget]
#   --min-nuget  只拷贝 6.0.36 运行时包（省约 4.8GB，U 盘空间紧张才用；完整缓存更保险）
# 前置: offline-kit/vs2026-layout 已下载（联网机运行 vs_Community_2026.exe --layout 生成）
#       offline-kit/installers 两个 .exe 已下载（见 离线机安装说明.txt 第一节）
# 可重复执行：已存在的部分自动跳过
set -e

REPO="$(cd "$(dirname "$0")/.." && pwd)"
KIT="$REPO/offline-kit"
MIN_NUGET=0
for a in "$@"; do
  case "$a" in
    --min-nuget) MIN_NUGET=1 ;;
    -*) echo "未知参数: $a"; exit 1 ;;
    *) KIT="$a" ;;
  esac
done

echo "== [1/5] 整理安装包目录"
mkdir -p "$KIT/01-安装包" "$KIT/02-VS2026离线安装"
for exe in "$KIT"/installers/*.exe; do
  [ -f "$exe" ] && mv -f "$exe" "$KIT/01-安装包/"
done
rmdir "$KIT/installers" 2>/dev/null || true
if [ -d "$KIT/vs2026-layout" ] && [ ! -d "$KIT/02-VS2026离线安装/vs2026-layout" ]; then
  mv "$KIT/vs2026-layout" "$KIT/02-VS2026离线安装/"
fi
[ -f "$KIT/vs_Community_2026.exe" ] && mv -f "$KIT/vs_Community_2026.exe" "$KIT/02-VS2026离线安装/" || true
ls "$KIT/01-安装包/" | sed 's/^/  /'

echo "== [2/5] 拷贝 NuGet 缓存"
NUGET_SRC="${USERPROFILE}/.nuget/packages"
NUGET_DST="$KIT/03-NuGet缓存/packages"
if [ ! -d "$NUGET_SRC" ]; then echo "错误: 未找到 $NUGET_SRC"; exit 1; fi
if [ -d "$NUGET_DST" ]; then
  echo "  已存在，跳过（更新请先删除 $NUGET_DST）"
elif [ $MIN_NUGET -eq 1 ]; then
  echo "  （--min-nuget 精简模式：只拷贝 6.0.36 运行时包，离线编译仅够用）"
  for p in microsoft.netcore.app.runtime.linux-x64 \
           microsoft.netcore.app.runtime.win-x64 \
           microsoft.aspnetcore.app.runtime.linux-x64 \
           microsoft.aspnetcore.app.runtime.win-x64; do
    if [ -d "$NUGET_SRC/$p/6.0.36" ]; then
      mkdir -p "$NUGET_DST/$p"
      cp -a "$NUGET_SRC/$p/6.0.36" "$NUGET_DST/$p/"
    else
      echo "  警告: 本机缓存缺 $p/6.0.36 —— 先在本机跑一次 win-x64 和 linux-x64 自包含发布再打包"
    fi
  done
else
  mkdir -p "$KIT/03-NuGet缓存"
  cp -a "$NUGET_SRC" "$NUGET_DST"
fi
du -sh "$NUGET_DST" | sed 's/^/  /'

echo "== [3/5] 拷贝源码（剔除 bin/obj/publish/tgz/现场配置备份）"
SRC_DST="$KIT/04-源码/B16_BufferC"
if [ -d "$SRC_DST/.git" ]; then
  echo "  已存在，跳过（更新请先删除 $SRC_DST）"
else
  mkdir -p "$SRC_DST"
  tar -C "$REPO" --exclude='bin' --exclude='obj' --exclude='publish' \
      --exclude='offline-kit' --exclude='*.tgz' --exclude='*.zip' --exclude='*.log' \
      --exclude='.vs' --exclude='TestResults' --exclude='wsl-field-config-backup.json' \
      -cf - . | tar -C "$SRC_DST" -xf -
fi
du -sh "$SRC_DST" | sed 's/^/  /'

echo "== [4/5] 生成离线机安装说明.txt"
cat > "$KIT/离线机安装说明.txt" <<'EOF'
BufferC 离线开发环境安装说明（全程无需联网）
==============================================

一、本包内容
  01-安装包/          .NET SDK 9.0.309 + ASP.NET Core Runtime 6.0.36 离线安装包
  02-VS2026离线安装/  VS Community 2026 离线安装（vs2026-layout）
  03-NuGet缓存/       NuGet 包缓存（离线编译必需，拷到本机固定路径）
  04-源码/            项目源码（git 仓库，含 Simulator 仿真器与部署脚本）
  校验清单.txt        安装包 SHA256 校验值（拷完 U 盘可核对）

二、离线机安装顺序
  1. 双击 01-安装包/dotnet-sdk-9.0.309-win-x64.exe（编译用）
  2. 双击 01-安装包/dotnet-hosting-6.0.36-win.exe（本机运行程序用，缺它 dotnet run 报
     "You must install .NET to run this application"）
  3. 进入 02-VS2026离线安装/vs2026-layout/，双击 vs_setup.exe 安装 VS
     （SmartScreen 拦截时点"更多信息"→"仍要运行"；约 20~40 分钟）
  4. 把 03-NuGet缓存/packages 整个拷到 C:\Users\<你的用户名>\.nuget\packages
     （没有 .nuget 目录就先建一个）

三、验证环境完整
  1. 命令行执行 dotnet --list-sdks 应看到 9.0.309；dotnet --list-runtimes 应看到 6.0.36
  2. VS 打开 04-源码/B16_BufferC/Bufferc.slnx，F5 运行 BufferC.Host 能启动
  3. 验证离线出 Linux 包（关键步骤）：
     dotnet publish src/BufferC.Host -r linux-x64 --self-contained -c Release -o publish/linux-x64
     成功后检查 publish/linux-x64/config.json 存在（缺它程序退出码 2）

四、现场日常流程
  1. VS 改代码 → F5 调试（连仿真器或真 PLC，见 deploy/现场使用手册.md）
  2. 测 OK → 按三.3 发布 linux-x64 → tar czf bufferc-linux-x64.tgz -C publish/linux-x64 .
  3. 部署服务器：见 deploy/部署说明.md（更新时先备份现场 config.json / bufferc.db）
  4. 联调预演：bash deploy/联调预演.sh（需离线机装 CentOS7 WSL）

五、常见问题
  - 还原/发布报找不到包：确认二.4 的缓存路径正确（必须放在 .nuget\packages）
  - VS Community 离线授权：首次启动可跳过登录，约 30 天内有效，到期需联网激活一次
  - 更新离线包：联网机重跑 deploy/制作离线开发包.sh 即可增量更新
EOF
echo "  已生成"

echo "== [5/5] 生成校验清单"
: > "$KIT/校验清单.txt"
for exe in "$KIT/01-安装包"/*.exe; do
  [ -f "$exe" ] && sha256sum "$exe" | sed "s#$KIT/##" >> "$KIT/校验清单.txt"
done
cat "$KIT/校验清单.txt" | sed 's/^/  /'

echo
echo "== 完成。离线开发包位置: $KIT"
du -sh "$KIT" | sed 's/^/  总计: /'
echo "  提醒: 整个文件夹拷到 U 盘（约需 16GB 容量）带到现场即可"
