using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Hanga.Sample.Controllers;
using UglyToad.PdfPig;

namespace Hanga.ChromiumCheck
{
    /// <summary>
    /// 基準と新しい結果の PDF を比べ、違いの一覧を作る(要件11.2)。
    /// 比べるもの: ページ数、ページサイズ(±1pt)、文字列、各文字の位置(±0.5pt)と描いたフォント。
    /// 文字の位置とフォントで、レイアウトのずれ・改ページ位置の変化・フォントの置き換わり(外字が別のフォントで描かれる等)を見つける。
    /// PDF を画像にするライブラリを増やさないため、画像では比べない(気になる違いは PDF を開いて目で確かめる)。
    /// </summary>
    internal sealed class ReportComparer
    {
        /// <summary>ページサイズの許容誤差(pt)。Chromium は内部で丸めるため(design.md「⑨」)。</summary>
        private const double PageSizeTolerance = 1.0;

        /// <summary>文字の位置の許容誤差(pt)。</summary>
        private const double PositionTolerance = 0.5;

        public List<string> Compare(string baselineDir, string currentDir)
        {
            var differences = new List<string>();
            foreach (string name in ReportsController.Names)
            {
                string basePdf = Path.Combine(baselineDir, name + ".pdf");
                if (!File.Exists(basePdf))
                {
                    differences.Add($"{name}: 基準に PDF がありません({basePdf})。");
                    continue;
                }

                var expected = Describe(File.ReadAllBytes(basePdf));
                var actual = Describe(File.ReadAllBytes(Path.Combine(currentDir, name + ".pdf")));
                if (expected.Count != actual.Count)
                {
                    differences.Add($"{name}: ページ数が違います(基準 {expected.Count}、今回 {actual.Count})。");
                }

                for (int i = 0; i < Math.Min(expected.Count, actual.Count); i++)
                {
                    differences.AddRange(ComparePage($"{name} {i + 1}ページ", expected[i], actual[i]));
                }
            }

            return differences;
        }

        private static IEnumerable<string> ComparePage(string label, PageInfo expected, PageInfo actual)
        {
            if (Math.Abs(expected.Width - actual.Width) > PageSizeTolerance || Math.Abs(expected.Height - actual.Height) > PageSizeTolerance)
            {
                yield return $"{label}: ページサイズが違います(基準 {expected.Width:0.##}×{expected.Height:0.##}pt、今回 {actual.Width:0.##}×{actual.Height:0.##}pt)。";
            }

            if (expected.Text != actual.Text)
            {
                yield return $"{label}: 文字列が違います。基準「{Excerpt(expected.Text, actual.Text)}」/ 今回「{Excerpt(actual.Text, expected.Text)}」";
                yield break; // 文字列が違えば、文字の位置の比較は意味をなさない
            }

            int moved = 0, fontChanged = 0;
            string? first = null;
            for (int i = 0; i < Math.Min(expected.Letters.Count, actual.Letters.Count); i++)
            {
                LetterInfo e = expected.Letters[i], a = actual.Letters[i];
                bool isMoved = Math.Abs(e.X - a.X) > PositionTolerance || Math.Abs(e.Y - a.Y) > PositionTolerance;
                bool isFontChanged = e.Font != a.Font;
                moved += isMoved ? 1 : 0;
                fontChanged += isFontChanged ? 1 : 0;
                if ((isMoved || isFontChanged) && first == null)
                {
                    first = string.Format(CultureInfo.InvariantCulture, "最初の違い「{0}」: 基準 {1} ({2:0.#}, {3:0.#}) / 今回 {4} ({5:0.#}, {6:0.#})", e.Value, e.Font, e.X, e.Y, a.Font, a.X, a.Y);
                }
            }

            if (moved > 0 || fontChanged > 0)
            {
                yield return $"{label}: 位置が {moved} 文字、フォントが {fontChanged} 文字違います。{first}";
            }
        }

        private static List<PageInfo> Describe(byte[] pdf)
        {
            using var document = PdfDocument.Open(pdf);
            return document.GetPages().Select(p => new PageInfo(
                p.Width,
                p.Height,
                string.Join(" ", p.GetWords().Select(w => w.Text)),
                p.Letters.Select(l => new LetterInfo(l.Value, FontFamily(l.FontName), l.StartBaseLine.X, l.StartBaseLine.Y)).ToList())).ToList();
        }

        /// <summary>フォント名からサブセットの接頭辞(<c>ABCDEF+</c>)を除く。</summary>
        private static string FontFamily(string? fontName)
        {
            string name = fontName ?? string.Empty;
            int plus = name.IndexOf('+');
            return plus == 6 ? name.Substring(plus + 1) : name;
        }

        /// <summary>2 つの文字列の、最初に食い違う位置の前後を抜き出す。</summary>
        private static string Excerpt(string text, string other)
        {
            int i = 0;
            while (i < text.Length && i < other.Length && text[i] == other[i])
            {
                i++;
            }

            int start = Math.Max(0, i - 15);
            return (start > 0 ? "…" : string.Empty) + text.Substring(start, Math.Min(40, text.Length - start)) + (start + 40 < text.Length ? "…" : string.Empty);
        }

        private sealed class PageInfo
        {
            public PageInfo(double width, double height, string text, List<LetterInfo> letters)
            {
                Width = width;
                Height = height;
                Text = text;
                Letters = letters;
            }

            public double Width { get; }

            public double Height { get; }

            public string Text { get; }

            public List<LetterInfo> Letters { get; }
        }

        private sealed class LetterInfo
        {
            public LetterInfo(string value, string font, double x, double y)
            {
                Value = value;
                Font = font;
                X = x;
                Y = y;
            }

            public string Value { get; }

            public string Font { get; }

            public double X { get; }

            public double Y { get; }
        }
    }
}
