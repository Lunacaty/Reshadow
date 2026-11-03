#!/usr/bin/env bash
# 打包发布版：只带官方卡池，不带测试数据与源数据。
#
# 排除清单与理由：
#   data/cards/         上游语料与测试集。运行时一句都不读
#                       （src/ 里引用次数为 0，只有 tools/ 脚本用过）。
#   data/packs/core-wb      17 张核心演示卡，属测试包。
#   data/packs/testset-v2   30 张测试卡。
#   data/pools/all-clans / custom-high-mana / official-standard
#                       这三个池引用上面两个测试包，打包后无法构建。
#   data/decks/         里面 my-forest-deck 绑的是 all-clans 池，一并排除。
#
# 保留：
#   data/packs/official-sv   982 张官方卡（含 87 张纹章/信仰定义）
#   data/pools/official      唯一可玩卡池
#   decks/                   预置牌组（全部只用官方卡）
set -e
cd "$(dirname "$0")"

GODOT="E:/Tools/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"
OUT="build"

taskkill //IM Reshadow.exe //F >/dev/null 2>&1 || true
sleep 1

echo "=== 清理旧包 ==="
rm -rf "$OUT"
mkdir -p "$OUT"

echo "=== 导出 ==="
"$GODOT" --path . --headless --export-release "Windows Desktop" \
  "E:/text/reshadow/game/build/Reshadow.exe" 2>&1 | tail -1

echo "=== 拷贝资源 ==="
# 只拷运行时真正读取的那一个目录。
# 源码里对 assets 的引用只有两处：CardArtLoader 的 res://assets/cards/<id>.png
# 与 ContentPaths.CardArtPath。其余 art / kernel / bg / frames / packs / *.json
# 在 src 与 tscn 里引用次数都是 0，属于开发期素材，进包只白占体积。
mkdir -p "$OUT/assets"
cp -r assets/cards "$OUT/assets/cards"
cp -r data   "$OUT/data"

echo "=== 剔除开发期资源 ==="
# 兜底：若上面的清单以后又放宽成整目录拷贝，这几条仍会把素材挡在外面。
rm -rf "$OUT/assets/kernel" "$OUT/assets/art" "$OUT/assets/bg"
rm -rf "$OUT/assets/frames" "$OUT/assets/packs" "$OUT/assets/en"
rm -f  "$OUT/assets"/*.json
rm -rf "$OUT/data/cards"
rm -rf "$OUT/data/packs/core-wb" "$OUT/data/packs/testset-v2"
rm -f  "$OUT/data/pools/all-clans.pool.json" \
       "$OUT/data/pools/custom-high-mana.pool.json" \
       "$OUT/data/pools/official-standard.pool.json"
rm -f  "$OUT/data/decks/my-forest-deck.deck.json"

echo "=== 拷贝牌组 ==="
mkdir -p "$OUT/decks"
cp decks/*.json "$OUT/decks/" 2>/dev/null || true
# my-forest-deck 绑的是 all-clans 池（测试包），打包后无法构建，必须剔除。
rm -f "$OUT/decks/my-forest-deck.deck.json"

echo
echo "=== 包内清单 ==="
echo "-- 卡包 --";  ls "$OUT/data/packs/"
echo "-- 卡池 --";  ls "$OUT/data/pools/"
echo "-- 牌组 --";  ls "$OUT/decks/"
echo "-- 卡图 --";  ls "$OUT/assets/cards/"*.png | wc -l
echo "-- assets 子项 --"; ls "$OUT/assets/"
echo "-- 体积 --";  du -sh "$OUT"
echo
echo PACKED