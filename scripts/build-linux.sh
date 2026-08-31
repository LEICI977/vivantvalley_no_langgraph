#!/usr/bin/env bash
set -euo pipefail

GAME_PATH="${GAME_PATH:-${1:-}}"
if [[ -z "$GAME_PATH" ]]; then
  echo "用法：GAME_PATH=/path/to/StardewValley ./scripts/build-linux.sh" >&2
  exit 2
fi

required=(
  StardewValley.dll
  StardewModdingAPI.dll
  StardewValley.GameData.dll
  MonoGame.Framework.dll
  xTile.dll
  0Harmony.dll
)
missing=0
for name in "${required[@]}"; do
  if [[ ! -f "$GAME_PATH/$name" && ! -f "$GAME_PATH/smapi-internal/$name" ]]; then
    echo "缺少：$GAME_PATH/$name" >&2
    missing=1
  fi
done
if (( missing )); then
  echo "请将已安装 SMAPI 的 Stardew Valley 目录上传到服务器后再编译。" >&2
  exit 1
fi

dotnet build VivantValley.csproj -c Release -p:GamePath="$GAME_PATH"

OUTPUT_DIR="bin/Release/net6.0"
DIST_DIR="dist"
PACKAGE_DIR="$DIST_DIR/VivantValley"
ZIP_PATH="$DIST_DIR/VivantValley-Release.zip"
rm -rf "$PACKAGE_DIR"
mkdir -p "$PACKAGE_DIR"
cp "$OUTPUT_DIR/VivantValley.dll" "$PACKAGE_DIR/"
cp manifest.json "$PACKAGE_DIR/"
for directory in i18n assets; do
  [[ -d "$OUTPUT_DIR/$directory" ]] && cp -R "$OUTPUT_DIR/$directory" "$PACKAGE_DIR/"
done
rm -f "$ZIP_PATH"
(cd "$DIST_DIR" && zip -qr "$(basename "$ZIP_PATH")" VivantValley)
echo "编译完成：$OUTPUT_DIR/VivantValley.dll"
echo "安装包：$ZIP_PATH"
