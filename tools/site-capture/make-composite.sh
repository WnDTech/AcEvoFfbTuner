#!/usr/bin/env bash
# Join a synced session (game.mp4 + app.mp4) into one clip.
# usage: make-composite.sh <session-dir> <name> <start-sec> <dur-sec> <layout: side|pip> [app-crop W:H:X:Y] [out-dir]
#   side = game left, app right (wide banner)   pip = game full frame, app inset bottom-right
set -euo pipefail
D="$1"; NAME="$2"; SS="$3"; DUR="$4"; LAYOUT="$5"
CROP="${6:-1584:961:8:31}"           # FFB Tuner window on the app monitor (title bar trimmed)
OUT="${7:-$(dirname "$0")/../../website/assets/media}"; mkdir -p "$OUT"
case "$LAYOUT" in
  side) FC="[0:v]fps=30,scale=-2:1080[g];[1:v]fps=30,crop=$CROP,scale=-2:1080[a];[g][a]hstack=2,scale=1920:-2[v]";;
  pip)  FC="[0:v]fps=30,scale=1920:1080[g];[1:v]fps=30,crop=$CROP,scale=780:-2,pad=iw+8:ih+8:4:4:color=0xFF7A1A[a];[g][a]overlay=W-w-28:H-h-28[v]";;
  *) echo "layout must be side or pip"; exit 1;;
esac
ffmpeg -y -loglevel error -ss "$SS" -t "$DUR" -i "$D/game.mp4" -ss "$SS" -t "$DUR" -i "$D/app.mp4" \
  -filter_complex "$FC" -map "[v]" -c:v libx264 -preset slow -crf 24 -pix_fmt yuv420p -movflags +faststart -an "$OUT/$NAME.mp4"
# Animated WebP for the site (plays everywhere, no autoplay rules)
ffmpeg -y -loglevel error -i "$OUT/$NAME.mp4" -vf "fps=15,scale=1280:-2:flags=lanczos" -c:v libwebp_anim -q:v 50 -compression_level 6 -loop 0 "$OUT/$NAME.webp"
ls -la "$OUT/$NAME".*
