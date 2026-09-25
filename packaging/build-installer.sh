#!/usr/bin/env bash
# Сборка инсталляторов BrainstormBuddy — две версии:
#   • Lite  → installer-inno/BrainstormBuddy-Setup-Lite.exe  (~0.5 ГБ): приложение + GigaAM.
#   • Full  → installer-inno/BrainstormBuddy-Setup-Full.exe  (~1 ГБ):   + офлайн Whisper.
# Шаги: self-contained publish → копируем модели → ISCC дважды (Lite без ключа, Full с /DIncludeWhisper).
set -euo pipefail
cd "$(dirname "$0")/.."

ISCC="${ISCC:-/c/Users/$USERNAME/AppData/Local/Programs/Inno Setup 6/ISCC.exe}"
WHISPER_NAME="ggml-large-v3-turbo-q5_0.bin"

echo "[1/4] Публикация self-contained (win-x64)…"
dotnet publish BrainstormBuddy/BrainstormBuddy.csproj -c Release -r win-x64 \
  --self-contained true -o publish/app-inno --nologo -v minimal

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
mkdir -p publish/app-inno/models
cp -f artifacts/gigaam/v2_ctc.onnx publish/app-inno/models/
cp -f artifacts/gigaam/labels.json  publish/app-inno/models/
# THIRD-PARTY-NOTICES кладём в свип publish\* (Source ..\..\THIRD-PARTY... ISCC молча не паковал).
cp -f THIRD-PARTY-NOTICES.txt publish/app-inno/

# Руководства (user/admin, docx+pdf) — в {app}\docs. Имена файлов несут версию продукта —
# берём её из csproj, чтобы старые *_2.5.x рядом в docs/manuals не попали в поставку.
BB_VER=$(grep -oP '<Version>\s*\K[^<\s]+' BrainstormBuddy/BrainstormBuddy.csproj)
if [ -z "$BB_VER" ]; then
  echo "ОШИБКА: не удалось прочитать <Version> из BrainstormBuddy/BrainstormBuddy.csproj" >&2
  exit 4
fi
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
  publish/app-inno/models/* | tr -d '\r' > installer-inno/SHA256SUMS.txt
echo "      installer-inno/SHA256SUMS.txt:"
sed 's/^/        /' installer-inno/SHA256SUMS.txt
ls -la installer-inno/BrainstormBuddy-Setup-*.exe
