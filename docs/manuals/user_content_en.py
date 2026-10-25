"""English user guide chapters assembled from _frag_en/*.py."""
import importlib.util as _ilu
from pathlib import Path as _P


def _load(name):
    p = _P(__file__).resolve().parent / "_frag_en" / name
    s = _ilu.spec_from_file_location("_frag_en_" + name.replace(".py", ""), p)
    m = _ilu.module_from_spec(s)
    s.loader.exec_module(m)
    return m


_MODS = [_load(n) for n in [
    "a1_start_overlay.py",
    "a2_api_llm.py",
    "a3_audio_stt.py",
    "a4_filetranscription.py",
    "a5_overlay_hotkeys_tray.py",
    "a8_memory.py",
    "a9_mcp.py",
    "a12_mcp_codex.py",
    "a10_images.py",
    "a6_summary_agents_diag.py",
    "a11_backup_search.py",
]]


def build(h, version):
    chapters = []
    for m in _MODS:
        chapters += m.chapters(h)
    return chapters
