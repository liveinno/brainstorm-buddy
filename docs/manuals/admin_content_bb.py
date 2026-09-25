"""Сгенерировано stitch_content.py из _frag/*. Не редактировать вручную."""
import importlib.util as _ilu
from pathlib import Path as _P


def _load(name):
    p = _P(__file__).resolve().parent / "_frag" / name
    s = _ilu.spec_from_file_location("_frag_" + name.replace(".py", ""), p)
    m = _ilu.module_from_spec(s)
    s.loader.exec_module(m)
    return m


_MODS = [_load(n) for n in ['a7_admin.py']]


def build(h, version):
    chapters = []
    for m in _MODS:
        chapters += m.chapters(h)
    return chapters
