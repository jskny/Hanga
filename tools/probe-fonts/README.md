# 判定用フォント(probe fonts)

字形の無い文字の検出(要件6.7、design.md「⑧」)に使う、小さなフォント2つを生成する。

- `probe-a.ttf`(`HangaProbeA`)・`probe-b.ttf`(`HangaProbeB`): すべての符号位置(サロゲートを除く U+0020〜U+10FFFF)を、
  1つの字形に割り当てる(cmap format 13)。A と B は字形の形だけが異なる。
- 画面の表示には使わない。ページの各文字を「要素のフォント + 判定用フォントA」と「要素のフォント + 判定用フォントB」で描き比べ、
  結果が異なれば、要素のフォント(外字用フォントを含む)に字形が無いと判定する。
- 生成物は `src/Hanga.Rendering/Fonts/` に置き、`Hanga.Rendering` の埋め込みリソースにしている。Hanga の一部として MIT License で配布する。

```bash
pip install fonttools
python3 tools/probe-fonts/make_probe_fonts.py src/Hanga.Rendering/Fonts
```
