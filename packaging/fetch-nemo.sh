#!/usr/bin/env bash
# Раскладка nemo-speech бандла для Nemotron-3-Diarization:
#   • nemo-speech-0.2.0-windows-x86_64-vulkan.zip — GitHub releases NVIDIA/NeMo-Speech.cpp
#     (vulkan-сборка покрывает и cpu-режим: ggml-cpu.dll + ggml-vulkan.dll внутри);
#   • bin/*.dll + bin/*.exe → artifacts/nemo-speech/ (плоско; include/lib/share не нужны —
#     publish берёт весь каталог в app-inno/nemo-speech, инсталлятор — в {app}\nemo-speech);
#   • GGUF-модель: источник artifacts/speaker/ (gitignored); для dev-запуска вне
#     репо-дерева копируем в %APPDATA%/BrainstormBuddy/models/ — резолвер смотрит туда
#     до {app}/models и artifacts/speaker.
# Идемпотентен: архив качается один раз (sha256 зафиксирован), bin перепаковывается
# только если ключевых dll нет в назначении.
set -euo pipefail
cd "$(dirname "$0")/.."

VER="0.2.0"
ZIP_NAME="nemo-speech-${VER}-windows-x86_64-vulkan.zip"
ZIP_URL="https://github.com/NVIDIA/NeMo-Speech.cpp/releases/download/v${VER}/${ZIP_NAME}"
ZIP_DIR=".work/nemo-dist"
ZIP_PATH="$ZIP_DIR/$ZIP_NAME"
# Проверенный хэш релизного архива (сверен с раздачей и .sha256 в .work/nemo-dist).
ZIP_SHA256="edf15a04ba98740aa0ab75ae4681ac7ad0a488b22b35ce2cbebd10590bb1c1e1"
DEST="artifacts/nemo-speech"

mkdir -p "$ZIP_DIR" "$DEST"

# 1) Архив: качаем только если файла нет или хэш не сошёлся.
if [ ! -f "$ZIP_PATH" ] || [ "$(sha256sum "$ZIP_PATH" | cut -d' ' -f1)" != "$ZIP_SHA256" ]; then
  echo "[fetch-nemo] Скачиваю $ZIP_URL …"
  curl -fL --retry 3 -o "$ZIP_PATH" "$ZIP_URL"
fi
# Сверяем напрямую, без `sha256sum -c`: в Git Bash это BusyBox-версия, и при
# верификации она читает файлы в text-mode (CRLF/0x1A), из-за чего ЛЮБОЙ валидный
# zip даёт ложный FAILED. Прямой пересчёт хэша бинарно корректен.
ACTUAL_SHA256="$(sha256sum "$ZIP_PATH" | cut -d' ' -f1)"
if [ "$ACTUAL_SHA256" != "$ZIP_SHA256" ]; then
  echo "[fetch-nemo] ОШИБКА: SHA-256 архива не совпал: $ACTUAL_SHA256 (ожидался $ZIP_SHA256)" >&2
  exit 1
fi
echo "[fetch-nemo] Архив ок: $ZIP_PATH"

# 2) bin/* → artifacts/nemo-speech/ (плоско, только dll+exe — без include/lib/share).
if [ -f "$DEST/nemo_speech_asr_c.dll" ] && [ -f "$DEST/ggml-vulkan.dll" ]; then
  echo "[fetch-nemo] $DEST уже заполнен — распаковка пропущена"
else
  unzip -jo "$ZIP_PATH" "nemo-speech-${VER}-windows-x86_64-vulkan/bin/*.dll" \
    "nemo-speech-${VER}-windows-x86_64-vulkan/bin/*.exe" -d "$DEST" >/dev/null
  echo "[fetch-nemo] Распаковано в $DEST ($(ls -1 "$DEST" | wc -l) файлов)"
fi

# 3) GGUF-модель (~82 МБ): dev-копия в %APPDATA%/BrainstormBuddy/models/ под
#    именем артефакта q8_0 (матчим по glob — регистр может отличаться).
shopt -s nullglob nocaseglob
GGUFS=(artifacts/speaker/Nemotron-3-Diarization.*.gguf)
if [ ${#GGUFS[@]} -eq 0 ]; then
  echo "[fetch-nemo] ! GGUF не найден (artifacts/speaker/Nemotron-3-Diarization.*.gguf) — положите с HF" >&2
elif [ -n "${APPDATA:-}" ]; then
  MODELS_DIR="$(cygpath "$APPDATA" 2>/dev/null)/BrainstormBuddy/models"
  mkdir -p "$MODELS_DIR"
  cp -f "${GGUFS[0]}" "$MODELS_DIR/Nemotron-3-Diarization.q8_0.gguf"
  echo "[fetch-nemo] Модель → $MODELS_DIR/Nemotron-3-Diarization.q8_0.gguf"
else
  echo "[fetch-nemo] APPDATA не задан — dev-копию модели пропускаю (artifacts/speaker/ резолвер найдёт сам)"
fi

echo "[fetch-nemo] Готово."
