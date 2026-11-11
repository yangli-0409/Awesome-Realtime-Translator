#!/bin/zsh
# 从 MakeAppIcon.swift 画出母图，切出 macOS 需要的全部尺寸，打包成 AppIcon.icns。
#
#   Tools/make-icon.sh
#
# 产物：Sources/AwesomeRealtimeTranslator/Resources/AppIcon.icns（已提交，
# 所以日常构建不需要跑这个脚本；只有改了图标设计才需要）。

set -euo pipefail
cd "$(dirname "$0")/.."

readonly WORK="$(mktemp -d)"
readonly MASTER="$WORK/icon-1024.png"
readonly ICONSET="$WORK/AppIcon.iconset"
readonly OUT="Sources/AwesomeRealtimeTranslator/Resources/AppIcon.icns"

trap 'rm -rf "$WORK"' EXIT

swift Tools/MakeAppIcon.swift "$MASTER"
mkdir -p "$ICONSET" "$(dirname "$OUT")"

# iconutil 要求的文件名是固定的
for spec in 16:icon_16x16 32:icon_16x16@2x 32:icon_32x32 64:icon_32x32@2x \
            128:icon_128x128 256:icon_128x128@2x 256:icon_256x256 512:icon_256x256@2x \
            512:icon_512x512 1024:icon_512x512@2x; do
  size="${spec%%:*}"
  name="${spec##*:}"
  sips -z "$size" "$size" "$MASTER" --out "$ICONSET/$name.png" >/dev/null
done

iconutil --convert icns "$ICONSET" --output "$OUT"
echo "已生成 $OUT"
