#!/usr/bin/env bash
# Сборка инсталляторов BrainstormBuddy — две версии:
#   • Lite  → installer-inno/BrainstormBuddy-Setup-Lite.exe  (~0.7 ГБ): приложение + GigaAM v3+v2.
#   • Full  → installer-inno/BrainstormBuddy-Setup-Full.exe  (~1.3 ГБ):  + офлайн Whisper.
# Шаги: self-contained publish → копируем модели → ISCC дважды (Lite без ключа, Full с /DIncludeWhisper).
set -euo pipefail
cd "$(dirname "$0")/.."

ISCC="${ISCC:-/c/Users/$USERNAME/AppData/Local/Programs/Inno Setup 6/ISCC.exe}"
WHISPER_NAME="ggml-large-v3-turbo-q5_0.bin"

# Чистим целевую папку publish полностью: dotnet publish инкрементален и НЕ удаляет
# файлы, которых больше нет в графе зависимостей. Иначе сироты вроде старого
# onnxruntime.dll (напр. из снятого sherpa-пакета) утекут в инсталлятор и перетрут
# DirectML-сборку — EntryPointNotFound OrtSessionOptionsAppendExecutionProvider_DML.
rm -rf publish/app-inno

echo "[1/4] Публикация self-contained (win-x64)…"
dotnet publish BrainstormBuddy/BrainstormBuddy.csproj -c Release -r win-x64 \
  --self-contained true -o publish/app-inno --nologo -v minimal

# Проверяем, что корневой onnxruntime.dll — сборка из DirectML-пакета (с экспортом
# OrtSessionOptionsAppendExecutionProvider_DML). Инкрементальный publish мог оставить
# чужую версию от старых native-пакетов → GPU-режим молча падал бы на CPU.
DML_ORT="${NUGET_PACKAGES:-$HOME/.nuget/packages}/microsoft.ml.onnxruntime.directml/1.24.4/runtimes/win-x64/native/onnxruntime.dll"
if [ -f "$DML_ORT" ] && [ -f publish/app-inno/onnxruntime.dll ]; then
  if ! cmp -s "$DML_ORT" publish/app-inno/onnxruntime.dll; then
    echo "ОШИБКА: publish/app-inno/onnxruntime.dll ≠ DirectML-сборка 1.24.4." >&2
    echo "  В каталоге затесался чужой onnxruntime.dll (см. историю: sherpa-onnx 1.28.2)." >&2
    echo "  Пересоберите чисто: rm -rf publish/app-inno && dotnet publish …" >&2
    exit 3
  fi
fi

# Проверяем System.Text.Json/Encodings.Web: транзитивные 10.0.2 (через
# Microsoft.Extensions.AI) при self-contained publish могли проиграть
# runtime-pack → в выходе лежала сборка 8.0.0.0 при deps.json=10.0.2 и
# приложение падало на старте. Пиннинг в csproj чинит, но проверку держим:
# файл должен быть версии 10.0.x, не 8.0.x.
for asm in System.Text.Json System.Text.Encodings.Web; do
  f="publish/app-inno/$asm.dll"
  if [ -f "$f" ]; then
    ver=$(powershell -NoProfile -Command "(Get-Item '$f').VersionInfo.FileVersion" | tr -d '\r')
    case "$ver" in
      10.0.*) ;;
      *) echo "ОШИБКА: $f версии $ver, ожидалась 10.0.x." >&2
         echo "  deps.json требует 10.0.2 — приложение упадёт на старте." >&2
         exit 4 ;;
    esac
  fi
done

echo "[2/4] Копирую офлайн-модель GigaAM + labels + лицензии…"
# Бэклог №20: artifacts/ в .gitignore → у свежего клона каталога нет, и cp роняет скрипт.
# Даём понятную ошибку вместо молчаливого падения на set -e.
if [ ! -f artifacts/gigaam/v2_ctc.onnx ] || [ ! -f artifacts/gigaam/labels.json ]; then
  echo "ОШИБКА: нет офлайн-модели GigaAM." >&2
  echo "  Положите в artifacts/gigaam/:" >&2
  echo "    v2_ctc.onnx   — экспорт GigaAM-CTC (tools/gigaam_export, см. docs/INSTALLER.md §dev)" >&2
  echo "    labels.json   — словарь меток" >&2
  echo "  Без них собирается только перенос publish\\, инсталлятор — нет." >&2
  exit 3
fi
# v3 — дефолтный движок (Audio.SttEngine="native3"): как и v2, обязательна — без неё
# свежая установка на чистом ПК ушла бы в remote. Отсутствие роняет сборку, как v2.
if [ ! -f artifacts/gigaam-v3/v3_e2e_ctc.int8.onnx ] || [ ! -f artifacts/gigaam-v3/v3_e2e_ctc_vocab.txt ]; then
  echo "ОШИБКА: нет модели GigaAM v3 (дефолтный STT-движок)." >&2
  echo "  Положите в artifacts/gigaam-v3/:" >&2
  echo "    v3_e2e_ctc.int8.onnx   — e2e CTC int8 (istupakov/gigaam-v3-onnx)" >&2
  echo "    v3_e2e_ctc_vocab.txt   — BPE-словарь" >&2
  exit 3
fi
mkdir -p publish/app-inno/models
cp -f artifacts/gigaam/v2_ctc.onnx publish/app-inno/models/
cp -f artifacts/gigaam/labels.json  publish/app-inno/models/
cp -f artifacts/gigaam-v3/v3_e2e_ctc.int8.onnx publish/app-inno/models/
cp -f artifacts/gigaam-v3/v3_e2e_ctc_vocab.txt publish/app-inno/models/
# Движок диаризации Nemotron (nemo-speech бандл + GGUF) — опциональный:
# отсутствие — WARN, сборка не падает (в .iss skipifsourcedoesntexist).
# Раскладку в artifacts/nemo-speech делает packaging/fetch-nemo.sh.
if [ -d artifacts/nemo-speech ]; then
  mkdir -p publish/app-inno/nemo-speech
  cp -f artifacts/nemo-speech/* publish/app-inno/nemo-speech/
  echo "      + nemo-speech бандл скопирован ($(ls -1 artifacts/nemo-speech | wc -l) файлов, Nemotron-диаризация)"
else
  echo "      ! artifacts/nemo-speech/ не найден — запустите packaging/fetch-nemo.sh (движок nemotron недоступен)"
fi
# Модель резолвится в models/Nemotron-3-Diarization.q8_0.gguf; исходник лежит в
# artifacts/speaker/ — матчим по glob (регистр суффикса может отличаться).
for f in artifacts/speaker/Nemotron-3-Diarization.*.gguf; do
  if [ -f "$f" ]; then
    cp -f "$f" publish/app-inno/models/Nemotron-3-Diarization.q8_0.gguf
    echo "      + Nemotron-3-Diarization GGUF скопирован"
  else
    echo "      ! Nemotron GGUF не найден в artifacts/speaker/ — движок nemotron без модели"
  fi
  break
done
# THIRD-PARTY-NOTICES кладём в свип publish\* (Source ..\..\THIRD-PARTY... ISCC молча не паковал).
cp -f THIRD-PARTY-NOTICES.txt publish/app-inno/

# Руководства (user/admin, docx+pdf) — в {app}\docs. Имена файлов несут версию продукта —
# берём её из csproj, чтобы старые *_2.5.x рядом в docs/manuals не попали в поставку.
BB_VER=$(grep -oP '<Version>\s*\K[^<\s]+' BrainstormBuddy/BrainstormBuddy.csproj)
if [ -z "$BB_VER" ]; then
  echo "ОШИБКА: не удалось прочитать <Version> из BrainstormBuddy/BrainstormBuddy.csproj" >&2
  exit 4
fi
# Чистим целевую папку: publish переиспользуется между версиями, и без чистки
# в инсталлятор утекут руководства прошлых версий (виден рост Setup.exe на ~40 МБ).
rm -rf publish/app-inno/docs
mkdir -p publish/app-inno/docs
shopt -s nullglob
MANUALS=(docs/manuals/Руководство_*_BrainstormBuddy_${BB_VER}.docx docs/manuals/Руководство_*_BrainstormBuddy_${BB_VER}.pdf)
if [ ${#MANUALS[@]} -ne 4 ]; then
  echo "ОШИБКА: в docs/manuals/ найдено ${#MANUALS[@]} файлов руководств для версии ${BB_VER} (нужно ровно 4)." >&2
  echo "  Сгенерируйте: python docs/manuals/build_manuals_bb.py" >&2
  exit 4
fi
cp -f "${MANUALS[@]}" publish/app-inno/docs/
echo "      + руководства ${BB_VER} скопированы в publish/app-inno/docs (4 файла)"

# Ищем модель Whisper для Full-сборки: сначала репозиторный artifacts/whisper, затем %APPDATA%.
WHISPER_SRC=""
CANDIDATES=("artifacts/whisper/$WHISPER_NAME")
if [ -n "${APPDATA:-}" ]; then
  CANDIDATES+=("$(cygpath "$APPDATA" 2>/dev/null)/BrainstormBuddy/models/$WHISPER_NAME")
fi
for c in "${CANDIDATES[@]}"; do
  [ -f "$c" ] && { WHISPER_SRC="$c"; break; }
done

BUILD_FULL=0
if [ -n "$WHISPER_SRC" ]; then
  echo "      + Whisper найден ($WHISPER_SRC) → будет Full-сборка"
  cp -f "$WHISPER_SRC" publish/app-inno/models/
  BUILD_FULL=1
else
  echo "      ! Whisper-модель не найдена (положи $WHISPER_NAME в artifacts/whisper/) — соберу только Lite"
fi

# E5-эмбеддинги памяти (multilingual-e5-small FP32, ~470 МБ) — опциональный бандл,
# только для Full (под IncludeWhisper в .iss). artifacts/e5/ нет → warn+skip:
# Full собирается, e5 докачивается по кнопке в настройках.
if [ "$BUILD_FULL" = "1" ]; then
  if [ -f "artifacts/e5/model.onnx" ]; then
    mkdir -p publish/app-inno/models/e5
    cp -f artifacts/e5/model.onnx publish/app-inno/models/e5/
    cp -f artifacts/e5/sentencepiece.bpe.model publish/app-inno/models/e5/ 2>/dev/null \
      || echo "      ! artifacts/e5/sentencepiece.bpe.model не найден — E5 без токенизера бесполезен"
    cp -f artifacts/e5/config.json artifacts/e5/tokenizer_config.json publish/app-inno/models/e5/ 2>/dev/null || true
    echo "      + E5-модель из artifacts/e5 → бандл в Full (память из коробки)"
  else
    echo "      ! artifacts/e5/model.onnx не найден — Full соберётся без e5 (память докачается по кнопке)"
  fi
fi

echo "[3/4] Компиляция Lite (только GigaAM)…"
# MSYS_NO_PATHCONV — иначе git-bash конвертирует аргумент /D... в путь и ISCC падает.
MSYS_NO_PATHCONV=1 "$ISCC" packaging/inno/BrainstormBuddy.iss

if [ "$BUILD_FULL" = "1" ]; then
  echo "      Компиляция Full (+ Whisper)…"
  MSYS_NO_PATHCONV=1 "$ISCC" /DIncludeWhisper packaging/inno/BrainstormBuddy.iss
fi

echo "[4/4] SHA256SUMS + готово:"
# Контрольные суммы релиза: инсталляторы + офлайн-модели. Пути пишем от корня репо —
# `sha256sum -c installer-inno/SHA256SUMS.txt` валиден из корня без cd.
# LF обязателен: CRLF-концы строк sha256sum -c читает как часть имени файла.
# `-b` ставит `*file` — без маркера -c на Windows читает файл в text-mode и режет по 0x1A.
sha256sum -b \
  installer-inno/BrainstormBuddy-Setup-*.exe \
  publish/app-inno/models/*.* \
  publish/app-inno/models/e5/* \
  publish/app-inno/nemo-speech/* | tr -d '\r' > installer-inno/SHA256SUMS.txt
echo "      installer-inno/SHA256SUMS.txt:"
sed 's/^/        /' installer-inno/SHA256SUMS.txt
ls -la installer-inno/BrainstormBuddy-Setup-*.exe
