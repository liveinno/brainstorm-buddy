import os
import sys
import time
import tempfile
import logging
from pathlib import Path
from contextlib import asynccontextmanager

import numpy as np
from fastapi import FastAPI, UploadFile, Form, HTTPException
from fastapi.responses import JSONResponse
from pydub import AudioSegment

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(name)s: %(message)s")
log = logging.getLogger("stt-bridge")

_models: dict[str, "GigaAMWrapper"] = {}

GIGAAM_VERSIONS = {
    "gigaam-v2": "v2_ctc",
    "gigaam-e2e": "v2_rnnt",
}
SUPPORTED_MODELS = tuple(GIGAAM_VERSIONS.keys())

# Only keep one model loaded at a time to save RAM
_MAX_LOADED_MODELS = 1


class GigaAMWrapper:
    def __init__(self, model_id: str):
        self.model_id = model_id
        self.pipeline = None

    def load(self):
        if self.pipeline is not None:
            return
        log.info(f"Loading GigaAM [{self.model_id}] version={GIGAAM_VERSIONS[self.model_id]}")
        import gigaam
        self.pipeline = gigaam.load_model(GIGAAM_VERSIONS[self.model_id])
        log.info(f"GigaAM [{self.model_id}] loaded")

    def transcribe(self, audio_path: str) -> str:
        seg = AudioSegment.from_file(audio_path)
        duration_ms = len(seg)
        max_chunk_ms = 24_000

        if duration_ms <= max_chunk_ms:
            return self._transcribe_chunk(audio_path)

        texts = []
        overlap_ms = 2000
        start_ms = 0
        while start_ms < duration_ms:
            end_ms = min(start_ms + max_chunk_ms, duration_ms)
            chunk = seg[start_ms:end_ms]
            with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as tmp:
                chunk.export(tmp.name, format="wav")
                text = self._transcribe_chunk(tmp.name)
                os.unlink(tmp.name)
            if text:
                texts.append(text)
            start_ms = end_ms - overlap_ms

        return " ".join(texts)

    def unload(self):
        self.pipeline = None
        import gc
        gc.collect()
        try:
            import torch
            torch.cuda.empty_cache()
        except:
            pass
        log.info(f"Model [{self.model_id}] unloaded")

    def _transcribe_chunk(self, path: str) -> str:
        result = self.pipeline.transcribe(path)
        return (result or "").strip()


@asynccontextmanager
async def lifespan(app: FastAPI):
    log.info("STT Bridge starting up")
    yield
    log.info("STT Bridge shutting down")
    _models.clear()


app = FastAPI(title="Local STT Bridge", version="1.0.0", lifespan=lifespan)


def get_model(model_id: str) -> GigaAMWrapper:
    # Unload models not in use to save RAM
    to_unload = [k for k in _models if k != model_id]
    for k in to_unload:
        log.info(f"Unloading model: {k}")
        _models[k].unload()
        del _models[k]

    if model_id not in _models:
        _models[model_id] = GigaAMWrapper(model_id)
    wrapper = _models[model_id]
    try:
        wrapper.load()
    except Exception as e:
        log.error(f"Failed to load model {model_id}: {e}")
        raise RuntimeError(f"Model loading failed: {e}")
    return wrapper


@app.post("/v1/audio/transcriptions")
async def transcribe(file: UploadFile, model: str = Form("gigaam-v2")):
    start_time = time.time()
    model_id = model

    log.info(f"Request: model={model_id}, file={file.filename}, size={file.size}")

    if model_id not in SUPPORTED_MODELS:
        raise HTTPException(status_code=400, detail=f"Unknown model: {model_id}, supported: {SUPPORTED_MODELS}")

    try:
        wrapper = get_model(model_id)
    except RuntimeError as e:
        raise HTTPException(status_code=503, detail=str(e))

    suffix = Path(file.filename).suffix if file.filename else ".wav"
    with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as tmp:
        content = await file.read()
        tmp.write(content)
        tmp_path = tmp.name

    try:
        text = wrapper.transcribe(tmp_path)
        elapsed = time.time() - start_time
        log.info(f"Response: model={model_id}, time={elapsed:.2f}s, text_len={len(text)}")
        return JSONResponse({"text": text})
    except Exception as e:
        log.error(f"Transcription failed: {e}")
        raise HTTPException(status_code=500, detail=str(e))
    finally:
        try:
            os.unlink(tmp_path)
        except:
            pass


@app.get("/v1/models")
async def list_models():
    available = []
    for m in SUPPORTED_MODELS:
        is_loaded = m in _models and _models[m].pipeline is not None
        available.append({
            "id": m,
            "object": "model",
            "loaded": is_loaded,
            "backend": "pytorch",
        })
    return {"object": "list", "data": available}


@app.get("/health")
async def health():
    return {"status": "ok", "models_loaded": list(_models.keys())}
