#!/usr/bin/env python3
"""Build the BrainstormBuddy DOCX and PDF manuals with Microsoft Word.

The script intentionally uses only Python's standard library plus Microsoft Word COM.
Run from any directory on Windows:
    py docs/manuals/build_manuals_bb.py
"""

from __future__ import annotations

import argparse
import html
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import re
import sys
import struct
import tempfile
import zipfile
from xml.etree import ElementTree
from datetime import date
from typing import Iterable, Sequence

PRODUCT = "BrainstormBuddy"
PUBLISHER = "Aigens"
WEBSITE = "https://aigens.ru/"
SUPPORT = "support@aigens.ru"
YEAR = "2026"
BASE = Path(__file__).resolve().parent
SCREENSHOTS = BASE / "screenshots" / "bb"


def _read_product_version() -> str:
    """Единый источник версии — Directory.Build.props (<Version>); литералов версии нет.

    Раньше здесь был молчаливый fallback «0.8.3»: при нечитаемом props руководства
    собирались со старой версией и уезжали в поставку. Теперь — fail-loud.
    """
    forced = os.environ.get("MANUAL_VERSION")
    if forced:
        return forced
    props = BASE.parent.parent / "BrainstormBuddy" / "BrainstormBuddy.csproj"
    try:
        text = props.read_text(encoding="utf-8-sig")
    except OSError as ex:
        raise SystemExit(f"ОШИБКА: не прочитан {props} ({ex}) — версия руководств не определена") from ex
    match = re.search(r"<Version>\s*([^<\s]+)\s*</Version>", text)
    if not match:
        raise SystemExit(f"ОШИБКА: не найден <Version> в {props} — версия руководств не определена")
    return match.group(1)


MONTHS_RU = (
    "января", "февраля", "марта", "апреля", "мая", "июня",
    "июля", "августа", "сентября", "октября", "ноября", "декабря",
)


def _edition_date() -> str:
    """Дата редакции документа: MANUAL_DATE=ГГГГ-ММ-ДД либо сегодняшняя локальная."""
    raw = os.environ.get("MANUAL_DATE", "").strip()
    if raw:
        year, month, day = (int(part) for part in raw.split("-"))
    else:
        today = date.today()
        year, month, day = today.year, today.month, today.day
    return f"{day} {MONTHS_RU[month - 1]} {year} г."


VERSION = _read_product_version()
EDITION_DATE = _edition_date()
USER_STEM = f"Руководство_пользователя_BrainstormBuddy_{VERSION}"
ADMIN_STEM = f"Руководство_администратора_BrainstormBuddy_{VERSION}"


class Html:
    """Small, deliberately boring HTML component set used by both manuals."""
    def __init__(self) -> None:
        self.figure_number = 0


    @staticmethod
    def p(text: str) -> str:
        return f"<p>{html.escape(str(text))}</p>"

    @staticmethod
    def h2(text: str) -> str:
        return f"<h2>{html.escape(str(text))}</h2>"

    @staticmethod
    def h3(text: str) -> str:
        return f"<h3>{html.escape(str(text))}</h3>"

    @staticmethod
    def ul(items: Iterable[str]) -> str:
        return "<ul>" + "".join(f"<li>{html.escape(str(x))}</li>" for x in items) + "</ul>"

    @staticmethod
    def ol(items: Iterable[str]) -> str:
        return "<ol>" + "".join(f"<li>{html.escape(str(x))}</li>" for x in items) + "</ol>"

    @staticmethod
    def table(headers: Sequence[str], rows: Iterable[Sequence[object]]) -> str:
        head = "".join(f"<th>{html.escape(str(x))}</th>" for x in headers)
        body = "".join(
            "<tr>" + "".join(f"<td>{html.escape(str(x))}</td>" for x in row) + "</tr>"
            for row in rows
        )
        return f"<table><thead><tr>{head}</tr></thead><tbody>{body}</tbody></table>"

    @staticmethod
    def callout(kind: str, title: str, text: str) -> str:
        safe_kind = kind if kind in {"info", "warn", "danger", "success"} else "info"
        return (
            f'<div class="callout {safe_kind}"><div class="callout-title">'
            f"{html.escape(str(title))}</div><div>{html.escape(str(text))}</div></div>"
        )

    def img(self, filename: str, caption: str, size: str = "wide") -> str:
        path = SCREENSHOTS / filename
        if not path.is_file():
            raise FileNotFoundError(f"Required screenshot is missing: {path}")
        safe_size = size if size in {"wide", "medium", "small"} else "wide"
        with path.open("rb") as stream:
            header = stream.read(24)
        if len(header) < 24 or header[:8] != b"\x89PNG\r\n\x1a\n":
            raise ValueError(f"Screenshot must be a PNG: {path}")
        source_width, source_height = struct.unpack(">II", header[16:24])
        max_width, max_height = {
            "wide": (635, 673),
            "medium": (446, 537),
            "small": (295, 397),
        }[safe_size]
        scale = min(max_width / source_width, max_height / source_height, 1.0)
        width = max(1, round(source_width * scale))
        height = max(1, round(source_height * scale))
        self.figure_number += 1
        numbered_caption = f"Рисунок {self.figure_number} — {caption}"
        return (
            f'<table class="figure {safe_size}"><tr><td><img src="{path.resolve().as_uri()}" '
            f'width="{width}" height="{height}" alt="{html.escape(numbered_caption, quote=True)}">'
            f'<div class="figcaption">{html.escape(numbered_caption)}</div></td></tr></table>'
        )

    @staticmethod
    def grid(items: Iterable[str]) -> str:
        def fit_to_column(fragment: str) -> str:
            def scale_image(match: re.Match[str]) -> str:
                width, height = int(match.group(1)), int(match.group(2))
                scale = min(295 / width, 397 / height, 1.0)
                return f'width="{max(1, round(width * scale))}" height="{max(1, round(height * scale))}"'

            return re.sub(r'width="(\d+)" height="(\d+)"', scale_image, fragment)

        figures = [fit_to_column(item) for item in items]
        rows = []
        for index in range(0, len(figures), 2):
            cells = figures[index:index + 2]
            if len(cells) == 1:
                cells.append("")
            rows.append("<tr>" + "".join(f"<td>{item}</td>" for item in cells) + "</tr>")
        return '<table class="figure-grid">' + "".join(rows) + "</table>"

    @staticmethod
    def kbd(text: str) -> str:
        return f'<span class="kbd">{html.escape(str(text))}</span>'

    @staticmethod
    def code(text: str) -> str:
        return f"<pre>{html.escape(str(text))}</pre>"


CSS = r"""
@page { size: A4 portrait; margin: 17mm 17mm 18mm 19mm; }
html, body { font-family: "Segoe UI", Arial, sans-serif; color: #28242a; font-size: 10.2pt; line-height: 1.42; }
body { margin: 0; }
p { margin: 0 0 7pt 0; }
h1 { color: #241f26; font-size: 23pt; line-height: 1.08; margin: 0 0 13pt 0; page-break-before: auto; page-break-after: avoid; border-bottom: 2.2pt solid #ff563e; padding-bottom: 7pt; }
h2 { color: #312a33; font-size: 15.5pt; line-height: 1.16; margin: 15pt 0 7pt 0; page-break-after: avoid; }
h3 { color: #5f4a3b; font-size: 11.5pt; margin: 11pt 0 5pt 0; page-break-after: avoid; }
ul, ol { margin: 3pt 0 9pt 19pt; padding: 0; }
li { margin: 0 0 3pt 0; }
table { border-collapse: collapse; width: 100%; margin: 7pt 0 12pt 0; font-size: 8.7pt; page-break-inside: auto; }
thead { display: table-header-group; }
tr { page-break-inside: avoid; }
th { background: #2b252d; color: white; text-align: left; padding: 6pt 6pt; border: 0.7pt solid #443c46; }
td { vertical-align: top; padding: 5pt 6pt; border: 0.7pt solid #d8d0d7; }
tbody tr:nth-child(even) td { background: #f7f3f1; }
.cover { height: 200mm; page-break-after: always; page-break-inside: avoid; color: #28242a; padding: 18mm 12mm 16mm 12mm; }
.cover-rule { width: 34mm; height: 4pt; background: #ff563e; margin: 0 0 22mm 0; }
.cover-kicker { color: #9b6418; font-size: 10pt; letter-spacing: 1.2pt; text-transform: uppercase; margin-bottom: 8mm; }
.cover h1 { color: #241f26; border: 0; padding: 0; margin: 0 0 7mm 0; font-size: 32pt; page-break-before: auto; }
.cover-subtitle { font-size: 17pt; color: #5c555a; max-width: 145mm; line-height: 1.3; }
.cover-meta { margin-top: 45mm; font-size: 10pt; color: #6c6269; }
.cover-version { color: #241f26; font-weight: 700; font-size: 12pt; }
.front { }
.front h1 { page-break-before: avoid; }
.doc-grid { width: 100%; border-collapse: separate; border-spacing: 0; }
.doc-grid td { border: 0; border-bottom: 0.6pt solid #ded6dc; padding: 6pt 3pt; }
.doc-grid td:first-child { width: 42mm; color: #6c6269; font-weight: 600; }
.toc-title { font-size: 22pt; font-weight: 700; color: #241f26; margin-bottom: 10pt; }
.toc-marker { color: #6f666d; }
.chapter { }
.callout { border-left: 4pt solid #6d91c7; background: #eef4fb; padding: 8pt 10pt; margin: 9pt 0 11pt 0; page-break-inside: avoid; }
.callout.warn { border-left-color: #dfa63d; background: #fff7e6; }
.callout.danger { border-left-color: #d94a3a; background: #fff0ee; }
.callout.success { border-left-color: #509267; background: #edf8f0; }
.callout-title { font-weight: 700; margin-bottom: 2pt; }
.figure { margin: 10pt auto 13pt auto; text-align: center; page-break-inside: avoid; }
.figure td { border: 0; padding: 0; text-align: center; vertical-align: top; }
.figure img { border: 0.7pt solid #d8d0d7; background: #171318; }
.figure.wide img { max-width: 168mm; max-height: 178mm; }
.figure.medium img { max-width: 118mm; max-height: 142mm; }
.figure.small img { max-width: 78mm; max-height: 105mm; }
.figcaption { font-size: 8.3pt; color: #6f666d; margin-top: 4pt; font-style: italic; }
.figure-grid { width: 100%; border: 0; margin: 7pt 0 12pt 0; page-break-inside: avoid; }
.figure-grid td { width: 50%; border: 0; padding: 4pt; text-align: center; vertical-align: top; }
.figure-grid .figure { margin: 0 auto 6pt auto; }
.figure-grid .figure img { max-width: 76mm; max-height: 102mm; }
.kbd { display: inline-block; border: 0.7pt solid #bcb4ba; background: #f5f1f3; padding: 1pt 4pt; font-family: Consolas, monospace; font-size: 8.7pt; }
pre { white-space: pre-wrap; word-wrap: break-word; background: #211d23; color: #f7f1ed; border-left: 4pt solid #ff563e; padding: 8pt 10pt; font-family: Consolas, monospace; font-size: 8.2pt; line-height: 1.3; page-break-inside: avoid; }
.smallprint { font-size: 8.3pt; color: #6f666d; }
.badge { background: #ff563e; color: white; font-weight: 700; padding: 2pt 6pt; }
.keep { page-break-inside: avoid; }
"""


def load_content(module_name: str):
    path = BASE / f"{module_name}.py"
    spec = importlib.util.spec_from_file_location(module_name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Cannot load {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    chapters = module.build(Html(), VERSION)
    if not isinstance(chapters, list) or not all(isinstance(x, tuple) and len(x) == 2 for x in chapters):
        raise TypeError(f"{path.name}: build(h, version) must return list[(title, html)]")
    return chapters




def cover(title: str, subtitle: str) -> str:
    return f"""
<div class="cover">
  <div class="cover-rule"></div>
  <div class="cover-kicker">{html.escape(PUBLISHER)} · официальная документация</div>
  <h1>{html.escape(title)}</h1>
  <div class="cover-subtitle">{html.escape(subtitle)}</div>
  <div class="cover-meta">
    <div class="cover-version">Версия продукта {VERSION}</div>
    <div>Редакция документа: {EDITION_DATE}</div>
    <div>© {YEAR} {html.escape(PUBLISHER)}</div>
  </div>
</div>"""


def front_matter(doc_kind: str, audience: str, purpose: str) -> str:
    rows = [
        ("Продукт", f"{PRODUCT} {VERSION}"),
        ("Тип документа", doc_kind),
        ("Аудитория", audience),
        ("Сайт", WEBSITE),
        ("Поддержка", SUPPORT),
    ]
    table_rows = "".join(
        f"<tr><td>{html.escape(a)}</td><td>{html.escape(b)}</td></tr>" for a, b in rows
    )
    return f"""
<div class="front">
  <h1 style="page-break-before: always;">О документе</h1>
  <table class="doc-grid">{table_rows}</table>
  <h2>Назначение</h2>
  <p>{html.escape(purpose)}</p>
  <div class="callout info"><div class="callout-title">Источник истины</div>
  <div>Руководство описывает фактические пользовательские и эксплуатационные возможности BrainstormBuddy {VERSION}. Внешний вид может незначительно отличаться из-за DPI, темы Windows и состава устройств.</div></div>
  <h2>Условные обозначения</h2>
  <table><thead><tr><th>Обозначение</th><th>Смысл</th></tr></thead><tbody>
  <tr><td>Полужирный текст</td><td>Название кнопки, поля, окна или пункта меню.</td></tr>
  <tr><td>Моноширинный текст</td><td>Команда, параметр, путь или значение конфигурации.</td></tr>
  <tr><td>Предупреждение</td><td>Действие с последствиями для записи, данных или совместимости.</td></tr>
  </tbody></table>
</div>
<div class="front" style="page-break-before: always;">
  <div class="toc-title">Оглавление</div>
  <p class="toc-marker">[[TOC]]</p>
</div>"""


def render_html(title: str, subtitle: str, kind: str, audience: str, purpose: str, chapters) -> str:
    body = [cover(title, subtitle), front_matter(kind, audience, purpose)]
    for index, (chapter_title, chapter_html) in enumerate(chapters):
        page_break = ' style="page-break-before: always;"' if index == 0 else ""
        protected_title = re.sub(
            r"^(\d+)\. ",
            lambda match: f"Глава&nbsp;{match.group(1)}.&nbsp;",
            html.escape(chapter_title),
            count=1,
        )
        body.append(f'<section class="chapter"><h1{page_break}>{protected_title}</h1>{chapter_html}</section>')
    return (
        '<!doctype html><html lang="ru"><head><meta charset="utf-8">'
        f'<title>{html.escape(title)}</title><style>{CSS}</style></head><body>'
        + "".join(body)
        + "</body></html>"
    )


def powershell_script(items: Sequence[tuple[Path, Path, Path, str]]) -> str:
    payload = []
    for source, docx, _pdf, short_title in items:
        payload.append(
            "@{Html=" + psq(str(source)) + ";Docx=" + psq(str(docx)) + ";Title=" + psq(short_title) + "}"
        )
    array = ",\n    ".join(payload)
    return f"""$ErrorActionPreference = 'Stop'
$items = @(
    {array}
)
$word = New-Object -ComObject Word.Application
$word.Visible = $false
$word.DisplayAlerts = 0
try {{
  foreach ($item in $items) {{
    $doc = $word.Documents.Open($item.Html, $false, $false)
    try {{
      foreach ($section in $doc.Sections) {{
        $section.PageSetup.TopMargin = $word.CentimetersToPoints(1.7)
        $section.PageSetup.BottomMargin = $word.CentimetersToPoints(1.8)
        $section.PageSetup.LeftMargin = $word.CentimetersToPoints(1.9)
        $section.PageSetup.RightMargin = $word.CentimetersToPoints(1.7)
        $section.PageSetup.DifferentFirstPageHeaderFooter = -1
        $header = $section.Headers.Item(1).Range
        $header.Text = 'BRAINSTORMBUDDY {VERSION}  ·  ' + $item.Title.ToUpperInvariant()
        $header.Font.Name = 'Segoe UI'
        $header.Font.Size = 7.5
        $header.Font.Color = 7368816
        $footer = $section.Footers.Item(1).Range
        $footer.Text = '© {YEAR} {PUBLISHER}   ·   '
        $footer.Font.Name = 'Segoe UI'
        $footer.Font.Size = 8
        $footer.ParagraphFormat.Alignment = 2
        $footer.Collapse(0)
        [void]$footer.Fields.Add($footer, -1, 'PAGE', $true)
      }}
      $range = $doc.Content
      if ($range.Find.Execute('[[TOC]]')) {{
        $marker = $range.Paragraphs.Item(1).Range
        $start = $marker.Start
        [void]$marker.Delete()
        $range.SetRange($start, $start)
        [void]$doc.TablesOfContents.Add($range, $true, 1, 3)
      }}
      foreach ($shape in @($doc.InlineShapes)) {{
        try {{
          if ($null -ne $shape.LinkFormat) {{
            $shape.LinkFormat.SavePictureWithDocument = $true
            $shape.LinkFormat.BreakLink()
          }}
        }} catch {{}}
      }}
      foreach ($shape in @($doc.Shapes)) {{
        try {{
          if ($null -ne $shape.LinkFormat) {{
            $shape.LinkFormat.SavePictureWithDocument = $true
            $shape.LinkFormat.BreakLink()
          }}
        }} catch {{}}
      }}
      [void]$doc.Repaginate()
      foreach ($toc in $doc.TablesOfContents) {{ [void]$toc.Update() }}
      [void]$doc.Fields.Update()
      $doc.SaveAs2($item.Docx, 16)
    }} finally {{
      $doc.Close(0)
      [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($doc)
    }}
  }}
}} finally {{
  $word.Quit()
  [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($word)
}}
"""


def export_powershell_script(items: Sequence[tuple[Path, Path]]) -> str:
    array = ",\n    ".join(
        "@{Docx=" + psq(str(docx)) + ";Pdf=" + psq(str(pdf)) + "}" for docx, pdf in items
    )
    return f"""$ErrorActionPreference = 'Stop'
$items = @(
    {array}
)
$word = New-Object -ComObject Word.Application
$word.Visible = $false
$word.DisplayAlerts = 0
try {{
  foreach ($item in $items) {{
    $doc = $word.Documents.Open($item.Docx, $false, $true)
    try {{
      [void]$doc.Repaginate()
      foreach ($toc in $doc.TablesOfContents) {{ [void]$toc.Update() }}
      [void]$doc.Fields.Update()
      $doc.ExportAsFixedFormat($item.Pdf, 17)
    }} finally {{
      $doc.Close(0)
      [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($doc)
    }}
  }}
}} finally {{
  $word.Quit()
  [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($word)
}}
"""


def stamp_docx_metadata(path: Path, title: str) -> None:
    """Set locale-independent OOXML core properties before PDF export."""
    namespaces = {
        "cp": "http://schemas.openxmlformats.org/package/2006/metadata/core-properties",
        "dc": "http://purl.org/dc/elements/1.1/",
        "dcterms": "http://purl.org/dc/terms/",
        "dcmitype": "http://purl.org/dc/dcmitype/",
        "xsi": "http://www.w3.org/2001/XMLSchema-instance",
    }
    for prefix, uri in namespaces.items():
        ElementTree.register_namespace(prefix, uri)
    core_name = "docProps/core.xml"
    replacement = path.with_suffix(".metadata.docx")
    with zipfile.ZipFile(path, "r") as source:
        core = ElementTree.fromstring(source.read(core_name))

        def set_text(prefix: str, name: str, value: str) -> None:
            tag = f"{{{namespaces[prefix]}}}{name}"
            element = core.find(tag)
            if element is None:
                element = ElementTree.SubElement(core, tag)
            element.text = value

        set_text("dc", "title", title)
        set_text("dc", "subject", f"{PRODUCT} {VERSION}")
        set_text("dc", "creator", PUBLISHER)
        set_text("cp", "lastModifiedBy", PUBLISHER)
        set_text("cp", "keywords", f"{PRODUCT}; BrainstormBuddy; руководство; {VERSION}")
        core_bytes = ElementTree.tostring(core, encoding="utf-8", xml_declaration=True)
        with zipfile.ZipFile(replacement, "w") as target:
            for member in source.infolist():
                target.writestr(member, core_bytes if member.filename == core_name else source.read(member.filename))
    replacement.replace(path)


def psq(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def build(keep_html: bool = False) -> None:
    if os.name != "nt":
        raise SystemExit("Microsoft Word COM build is supported only on Windows")
    if not SCREENSHOTS.is_dir():
        raise FileNotFoundError(SCREENSHOTS)
    user_chapters = load_content("user_content_bb")
    admin_chapters = load_content("admin_content_bb")
    if len(user_chapters) < 10 or len(admin_chapters) < 9:
        raise ValueError("Manual content is incomplete: expected at least 10/9 chapters")

    user_html = render_html(
        "Руководство пользователя",
        "Помощник на онлайн-совещаниях: оверлей, распознавание речи и ответы LLM",
        "Руководство пользователя",
        "Пользователи BrainstormBuddy и специалисты поддержки",
        "Пошаговая работа со всеми пользовательскими режимами и элементами текущей версии.",
        user_chapters,
    )
    admin_html = render_html(
        "Руководство администратора",
        "Развёртывание, конфигурация, сопровождение и диагностика",
        "Руководство администратора",
        "Системные администраторы, инженеры сопровождения и специалисты ИБ",
        "Воспроизводимое развёртывание и сопровождение BrainstormBuddy в управляемой среде Windows.",
        admin_chapters,
    )

    user_docx, user_pdf = BASE / f"{USER_STEM}.docx", BASE / f"{USER_STEM}.pdf"
    admin_docx, admin_pdf = BASE / f"{ADMIN_STEM}.docx", BASE / f"{ADMIN_STEM}.pdf"
    with tempfile.TemporaryDirectory(prefix="bb-manuals-") as tmp_name:
        tmp = Path(tmp_name)
        user_source = tmp / "user.html"
        admin_source = tmp / "admin.html"
        user_source.write_text(user_html, encoding="utf-8-sig")
        admin_source.write_text(admin_html, encoding="utf-8-sig")
        ps1 = tmp / "convert.ps1"
        ps1.write_text(
            powershell_script(
                [
                    (user_source, user_docx, user_pdf, "Руководство пользователя"),
                    (admin_source, admin_docx, admin_pdf, "Руководство администратора"),
                ]
            ),
            encoding="utf-8-sig",
        )
        subprocess.run(
            ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(ps1)],
            check=True,
            cwd=BASE,
        )
        stamp_docx_metadata(user_docx, "Руководство пользователя")
        stamp_docx_metadata(admin_docx, "Руководство администратора")
        ps1.write_text(
            export_powershell_script([(user_docx, user_pdf), (admin_docx, admin_pdf)]),
            encoding="utf-8-sig",
        )
        subprocess.run(
            ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(ps1)],
            check=True,
            cwd=BASE,
        )
        if keep_html:
            (BASE / f"{USER_STEM}.html").write_text(user_html, encoding="utf-8-sig")
            (BASE / f"{ADMIN_STEM}.html").write_text(admin_html, encoding="utf-8-sig")

    for path in (user_docx, user_pdf, admin_docx, admin_pdf):
        if not path.is_file() or path.stat().st_size < 20_000:
            raise RuntimeError(f"Build did not produce a valid-looking file: {path}")
        print(f"{path.name}\t{path.stat().st_size} bytes")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--keep-html", action="store_true", help="keep intermediate HTML beside the manuals")
    args = parser.parse_args()
    build(args.keep_html)


if __name__ == "__main__":
    main()
