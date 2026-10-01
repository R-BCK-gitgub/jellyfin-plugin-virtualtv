#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/Jellyfin.Plugin.VirtualTV/Assets/Bootstrap"
FONT="/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"

if ! command -v ffmpeg >/dev/null 2>&1; then
  echo "ffmpeg is required to generate the Virtual TV bootstrap videos." >&2
  exit 1
fi

if [[ ! -f "$FONT" ]]; then
  echo "DejaVu Sans was not found at $FONT." >&2
  exit 1
fi

mkdir -p "$OUT"

COMMON_VIDEO=(
  -f lavfi -i "color=c=black:s=1280x720:r=24:d=10"
  -f lavfi -i "anullsrc=r=48000:cl=stereo"
  -t 10
  -c:v libx264
  -profile:v main
  -pix_fmt yuv420p
  -preset veryslow
  -g 48
  -c:a aac
  -b:a 16k
  -movflags +faststart
  -shortest
)

ffmpeg -hide_banner -loglevel error -nostdin -y   "${COMMON_VIDEO[@]}"   -crf 35   "$OUT/virtualtv-bootstrap-black-10s.mp4"

ffmpeg -hide_banner -loglevel error -nostdin -y   -f lavfi -i "color=c=black:s=1280x720:r=24:d=10"   -f lavfi -i "anullsrc=r=48000:cl=stereo"   -vf "drawtext=fontfile=$FONT:text='Loading Virtual TV...':fontcolor=white:fontsize=48:x=(w-text_w)/2:y=(h-text_h)/2"   -t 10   -c:v libx264   -profile:v main   -pix_fmt yuv420p   -crf 32   -preset veryslow   -g 48   -c:a aac   -b:a 16k   -movflags +faststart   -shortest   "$OUT/virtualtv-loading-10s.mp4"

echo "Generated Virtual TV bootstrap videos in $OUT"
