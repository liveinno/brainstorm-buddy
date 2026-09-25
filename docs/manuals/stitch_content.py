"""Сшивает _frag/aN_*.py в user_content_bb.py / admin_content_bb.py.

Каждый фрагмент экспортирует chapters(h) -> list[(title, html)].
Генерируемые *_bb.py грузят фрагменты по пути (не импорт-пакет).
"""
import importlib.util
import sys
from pathlib import Path

BASE = Path(__file__).resolve().parent
FRAG = BASE / "_frag"

USER_FRAGS = [
    "a1_start_overlay.py",
    "a2_api_llm.py",
    "a3_audio_stt.py",
    "a4_filetranscription.py",
    "a5_overlay_hotkeys_tray.py",
    "a6_summary_agents_diag.py",
]
ADMIN_FRAGS = [
    "a7_admin.py",
]

HEADER = '"""Сгенерировано stitch_content.py из _frag/*. Не редактировать вручную."""\nimport importlib.util as _ilu\nfrom pathlib import Path as _P\n\n\ndef _load(name):\n    p = _P(__file__).resolve().parent / "_frag" / name\n    s = _ilu.spec_from_file_location("_frag_" + name.replace(".py", ""), p)\n    m = _ilu.module_from_spec(s)\n    s.loader.exec_module(m)\n    return m\n\n\n_MODS = [_load(n) for n in {frags!r}]\n\n\ndef build(h, version):\n    chapters = []\n    for m in _MODS:\n        chapters += m.chapters(h)\n    return chapters\n'


def gen(out_name: str, frags: list[str]) -> None:
    missing = [f for f in frags if not (FRAG / f).is_file()]
    if missing:
        print(f"MISSING for {out_name}: {missing}", file=sys.stderr)
        sys.exit(1)
    (BASE / out_name).write_text(HEADER.format(frags=frags), encoding="utf-8")
    print(f"wrote {out_name} <- {len(frags)} frags")


def main() -> None:
    gen("user_content_bb.py", USER_FRAGS)
    gen("admin_content_bb.py", ADMIN_FRAGS)


if __name__ == "__main__":
    main()
