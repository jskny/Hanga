# 字形の無い文字の検出に使う判定用フォント(probe-a.ttf / probe-b.ttf)を生成する。
# すべての符号位置(サロゲートを除く U+0020〜U+10FFFF)を、1つの字形に割り当てる(cmap format 13)。
# A と B は字形の形だけが異なる。使い方: pip install fonttools && python3 make_probe_fonts.py
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib.tables._c_m_a_p import CmapSubtable


def rects(rs):
    pen = TTGlyphPen(None)
    for (x0, y0, x1, y1) in rs:
        pen.moveTo((x0, y0)); pen.lineTo((x0, y1)); pen.lineTo((x1, y1)); pen.lineTo((x1, y0)); pen.closePath()
    return pen.glyph()


def make(name, rs, path):
    fb = FontBuilder(1000, isTTF=True)
    fb.setupGlyphOrder([".notdef", "probe"])
    fb.setupCharacterMap({0x20: "probe"})
    fb.setupGlyf({".notdef": TTGlyphPen(None).glyph(), "probe": rects(rs)})
    fb.setupHorizontalMetrics({".notdef": (1000, 0), "probe": (1000, 100)})
    fb.setupHorizontalHeader(ascent=880, descent=-120)
    fb.setupNameTable({"familyName": name, "styleName": "Regular"})
    fb.setupOS2(sTypoAscender=880, sTypoDescender=-120, usWinAscent=880, usWinDescent=120)
    fb.setupPost()
    t = CmapSubtable.newSubtable(13)
    t.platformID, t.platEncID, t.language = 3, 10, 0
    t.cmap = {cp: "probe" for cp in list(range(0x20, 0xD800)) + list(range(0xE000, 0x110000))}
    fb.font["cmap"].tables = [t]
    fb.font.save(path)


make("HangaProbeA", [(100, 0, 900, 800)], "probe-a.ttf")
make("HangaProbeB", [(100, 0, 300, 800), (700, 0, 900, 800)], "probe-b.ttf")
