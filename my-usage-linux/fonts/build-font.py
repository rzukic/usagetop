#!/usr/bin/env python3
"""Builds usagetop-marks.otf: the providers' marks as characters of a font.

A bar that shows text (Waybar, swaybar, i3bar, polybar, Xfce's Generic Monitor)
cannot show a picture beside it, but it can show text in any installed font.
So each mark is a character in the Private Use Area, drawn from the same SVG the
GNOME extension uses, and the bar's text puts it in front of the limit:
<span font_family="usagetop marks"></span> Weekly 11%. A character takes
the bar's own colour and size, as the letters beside it do.

    python3 -m venv /tmp/fontvenv && /tmp/fontvenv/bin/pip install fonttools
    /tmp/fontvenv/bin/python my-usage-linux/fonts/build-font.py

The font is committed, so a release needs no Python. The code points here and
in Status.cs (Marks) must agree.
"""
import pathlib
import re

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.t2CharStringPen import T2CharStringPen
from fontTools.pens.transformPen import TransformPen
from fontTools.svgLib.path import parse_path

HERE = pathlib.Path(__file__).resolve().parent
ICONS = HERE.parent / "gnome-extension" / "usagetop@rzukic.github.io" / "icons"
OUT = HERE / "usagetop-marks.otf"

# Provider key -> code point. Never renumber: installed copies and old apps use them.
MARKS = {
    "claude": 0xE000,
    "chatgpt": 0xE001,
    "mistral": 0xE002,
    "kimi": 0xE003,
    "muse": 0xE004,
    "antigravity": 0xE005,
}

UPM = 1000
# A mark is about as tall as a capital and a little more, standing on the
# baseline's shoulder as an icon beside text does: from just under the
# baseline to just over the cap height.
SIZE = 860
BOTTOM = -110
SIDE = 50
ASCENT, DESCENT = 900, -250


def glyph(svg_path: pathlib.Path):
    svg = svg_path.read_text()
    view = [float(v) for v in re.search(r'viewBox="([^"]+)"', svg).group(1).split()]
    paths = re.findall(r'<path[^>]* d="([^"]+)"', svg)
    scale = SIZE / max(view[2], view[3])
    width = round(view[2] * scale) + 2 * SIDE
    pen = T2CharStringPen(width, None)
    # SVG's y runs down from the top of its box; a font's runs up from the baseline.
    flip = TransformPen(pen, (scale, 0, 0, -scale, SIDE - view[0] * scale, BOTTOM + (view[1] + view[3]) * scale))
    for d in paths:
        parse_path(d, flip)
    return width, pen.getCharString()


def main():
    names = [".notdef", "space"] + [f"mark.{key}" for key in MARKS]
    fb = FontBuilder(UPM, isTTF=False)
    fb.setupGlyphOrder(names)
    fb.setupCharacterMap({0x20: "space", **{code: f"mark.{key}" for key, code in MARKS.items()}})

    notdef = T2CharStringPen(500, None)
    space = T2CharStringPen(250, None)
    charstrings = {".notdef": notdef.getCharString(), "space": space.getCharString()}
    metrics = {".notdef": (500, 0), "space": (250, 0)}
    for key in MARKS:
        width, charstring = glyph(ICONS / f"usagetop-{key}-symbolic.svg")
        charstrings[f"mark.{key}"] = charstring
        metrics[f"mark.{key}"] = (width, SIDE)

    family = "usagetop marks"
    fb.setupCFF("usagetopmarks-Regular", {"FullName": family}, charstrings, {})
    fb.setupHorizontalMetrics(metrics)
    fb.setupHorizontalHeader(ascent=ASCENT, descent=DESCENT)
    fb.setupNameTable({
        "familyName": family,
        "styleName": "Regular",
        "uniqueFontIdentifier": "usagetop marks Regular",
        "fullName": family,
        "psName": "usagetopmarks-Regular",
        "version": "Version 1.0",
        "copyright": "The marks are their owners' trademarks; shown to name each provider.",
    })
    fb.setupOS2(sTypoAscender=ASCENT, sTypoDescender=DESCENT, usWinAscent=ASCENT, usWinDescent=-DESCENT,
                sCapHeight=700, sxHeight=500)
    fb.setupPost()
    fb.save(OUT)
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
