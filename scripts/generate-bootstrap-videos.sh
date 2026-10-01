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

# Actual Live TV bootstrap: a long transport stream, not a finite MP4 DirectPlay source.
# It is read at native frame rate by Jellyfin and Virtual TV hands off after confirmed
# playback progress + 5 seconds, well before this 60-second runway reaches EOF.
ffmpeg -hide_banner -loglevel error -nostdin -y   -f lavfi -i "color=c=black:s=1280x720:r=24:d=60"   -f lavfi -i "anullsrc=r=48000:cl=stereo"   -vf "drawtext=fontfile=$FONT:text='Loading Virtual TV...':fontcolor=white:fontsize=48:x=(w-text_w)/2:y=(h-text_h)/2"   -t 60   -c:v libx264   -profile:v main   -pix_fmt yuv420p   -crf 35   -preset veryfast   -g 48   -keyint_min 48   -sc_threshold 0   -c:a aac   -b:a 32k   -ar 48000   -ac 2   -mpegts_flags +resend_headers   -f mpegts   "$OUT/virtualtv-loading-live-60s.ts"

echo "Generated Virtual TV bootstrap media in $OUT"
