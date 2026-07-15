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
mkdir -p publish/app-inno/models
cp -f artifacts/gigaam/v2_ctc.onnx publish/app-inno/models/
cp -f artifacts/gigaam/labels.json  publish/app-inno/models/
# THIRD-PARTY-NOTICES кладём в свип publish\* (Source ..\..\THIRD-PARTY... ISCC молча не паковал).
cp -f THIRD-PARTY-NOTICES.txt publish/app-inno/

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

echo "[4/4] Готово:"
ls -la installer-inno/BrainstormBuddy-Setup-*.exe
