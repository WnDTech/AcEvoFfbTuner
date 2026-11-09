#!/usr/bin/env bash
# Turn a captured frame folder into web loops: <name>.mp4 (H.264), <name>.webm (VP9), <name>.jpg (poster).
# usage: make-clip.sh <frame-dir> <name> [start-sec=0] [duration-sec=12] [out-dir=../../website/assets/media]
#        [crop=W:H:X:Y  (default: strip the Windows title bar/borders)]
set -euo pipefail
IN="$1"; NAME="$2"; SS="${3:-0}"; DUR="${4:-12}"
OUT="${5:-$(dirname "$0")/../../website/assets/media}"; mkdir -p "$OUT"
CROP="${6:-iw-16:ih-39:8:31}"
VF="crop=$CROP,scale=1280:-2:flags=lanczos,format=yuv420p"
ffmpeg -y -loglevel error -framerate ${FPS:-10} -i "$IN/f%06d.jpg" -ss "$SS" -t "$DUR" -vf "$VF,fps=${FPS:-10}" \
  -c:v libx264 -preset slow -crf 26 -movflags +faststart -an "$OUT/$NAME.mp4"
ffmpeg -y -loglevel error -i "$OUT/$NAME.mp4" -c:v libvpx-vp9 -b:v 0 -crf 38 -row-mt 1 -an "$OUT/$NAME.webm"
ffmpeg -y -loglevel error -i "$OUT/$NAME.mp4" -frames:v 1 -q:v 3 "$OUT/$NAME.jpg"
ls -la "$OUT/$NAME".*
