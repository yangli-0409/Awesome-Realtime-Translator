#!/usr/bin/env bash
# 下载模型（macOS / Linux 版，和 fetch-models.ps1 等价）。
#
# 这个工程是在 macOS 上开发的：Core 的单测能在这里真跑，包括用真实模型
# 跑翻译。所以开发机也需要同一份模型。
set -euo pipefail

WHISPER_MODEL="${1:-base}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODELS="$ROOT/models"
mkdir -p "$MODELS/opus-mt-en-zh/onnx"

fetch() {
  local url="$1" dest="$2" label="$3"
  if [[ -f "$dest" ]]; then
    printf '  已存在，跳过：%s (%s)\n' "$label" "$(du -h "$dest" | cut -f1)"
    return
  fi
  printf '  下载 %s …\n' "$label"
  # 先下到 .part 再改名，中断不会留下半截文件
  curl -fsSL --retry 3 -o "$dest.part" "$url"
  mv "$dest.part" "$dest"
  printf '    完成（%s）\n' "$(du -h "$dest" | cut -f1)"
}

echo
echo "[1/2] 语音识别模型 whisper-$WHISPER_MODEL"
fetch "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-$WHISPER_MODEL.bin" \
      "$MODELS/ggml-base.bin" "ggml-$WHISPER_MODEL.bin"

echo
echo "[2/2] 翻译模型 opus-mt-en-zh"
BASE="https://huggingface.co/onnx-community/opus-mt-en-zh/resolve/main"
for f in vocab.json source.spm target.spm config.json; do
  fetch "$BASE/$f" "$MODELS/opus-mt-en-zh/$f" "$f"
done
for f in encoder_model_quantized.onnx decoder_model_quantized.onnx; do
  fetch "$BASE/onnx/$f" "$MODELS/opus-mt-en-zh/onnx/$f" "$f"
done

echo
echo "全部就绪，模型在 $MODELS"
