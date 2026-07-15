#!/usr/bin/env python3
"""
Экспорт GigaAM v2 CTC → ONNX + снятие эталона для валидации C#-движка.

Запускать ВНУТРИ контейнера, где установлен пакет `gigaam` (наш LocalSttBridge):
    docker cp export_onnx.py brainstorm-local-stt:/tmp/export_onnx.py
    docker exec brainstorm-local-stt python /tmp/export_onnx.py /tmp/gout
    docker cp brainstorm-local-stt:/tmp/gout/. ./artifacts/gigaam/

Выдаёт в out_dir:
    v2_ctc.onnx        — модель (вход: features[1,64,T], feature_lengths[1]; выход: log_probs[1,T',34])
    labels.json        — 33 символа (пробел + русский алфавит); CTC-blank = индекс 33 (vocab=34)
    frontend_meta.json — параметры мел-фронтенда (для повторения в C#)
    test.wav           — детерминированный тестовый сигнал
    ref_features.npy    — эталон входа ONNX  (1,64,301)  ← C# должен совпасть
    ref_logprobs.npy    — эталон выхода ONNX (1,76,34)
    ref_text.txt        — эталонный текст декода

Фронтенд (torchaudio MelSpectrogram + log):
    sample_rate=16000, n_fft=400, win_length=400, hop_length=160, n_mels=64,
    window=hann, power=2.0, mel_scale=htk, f_min=0, f_max=8000, norm=None,
    затем ln(clamp(x, 1e-9, 1e9)).

GPU fp16: на CPU-контейнере экспортится fp32. Для fp16 гонять на машине с CUDA
(gigaam.load_model('v2_ctc', fp16_encoder=True, device='cuda')).
"""
import sys, os, json, wave
import numpy as np
import gigaam


def main(out_dir: str):
    os.makedirs(out_dir, exist_ok=True)

    # 1) детерминированный тестовый сигнал (речеподобный, seed фиксирован)
    sr, dur = 16000, 3.0
    n = int(sr * dur); t = np.arange(n) / sr
    sig = 0.3 * np.sin(2*np.pi*180*t) * (0.5 + 0.5*np.sin(2*np.pi*3*t))
    sig += 0.2 * np.sin(2*np.pi*320*t) * (0.5 + 0.5*np.sin(2*np.pi*5*t))
    sig += 0.05 * np.random.RandomState(1).randn(n)
    sig = (sig / np.max(np.abs(sig)) * 0.8 * 32767).astype(np.int16)
    with wave.open(os.path.join(out_dir, "test.wav"), "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(sr); w.writeframes(sig.tobytes())

    # 2) модель + экспорт
    m = gigaam.load_model("v2_ctc")
    m.to_onnx(out_dir)  # → v2_ctc.onnx

    # 3) словарь
    labels = list(m.cfg.decoding.labels) if "labels" in m.cfg.decoding else None
    if labels:
        json.dump(labels, open(os.path.join(out_dir, "labels.json"), "w"), ensure_ascii=False)

    # 4) эталон: вход ONNX = preprocessor(wav); выход = forward_for_export
    wav, length = m.prepare_wav(os.path.join(out_dir, "test.wav"))
    feats, flen = m.preprocessor(wav, length)
    np.save(os.path.join(out_dir, "ref_features.npy"), feats.detach().cpu().numpy().astype(np.float32))
    logp = m.forward_for_export(feats, flen)
    np.save(os.path.join(out_dir, "ref_logprobs.npy"), logp.detach().cpu().numpy().astype(np.float32))
    text = m.decoding.decode(m.head, m.encoder(feats, flen)[0], flen)[0]
    open(os.path.join(out_dir, "ref_text.txt"), "w").write(text)

    json.dump({"sample_rate": 16000, "n_fft": 400, "win_length": 400, "hop_length": 160,
               "n_mels": 64, "window": "hann", "power": 2.0, "log": "ln(clamp(x,1e-9,1e9))",
               "mel_scale": "htk", "f_min": 0.0, "f_max": 8000.0, "norm": None,
               "vocab": 34, "blank_index": 33},
              open(os.path.join(out_dir, "frontend_meta.json"), "w"), ensure_ascii=False, indent=2)
    print("exported to", out_dir)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ".")
