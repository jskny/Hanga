using System.Collections.Generic;
using System.IO;
using System.Linq;
using UglyToad.PdfPig;

namespace Hanga.TestSupport
{
    /// <summary>PDF の性質(ページ数・ページサイズ・文字列)を取り出す。PDF のバイト列の完全一致では比べないため(design.md「テスト戦略」)。</summary>
    public sealed class PdfInspector
    {
        private PdfInspector(int pageCount, IReadOnlyList<(double Width, double Height)> pageSizes, IReadOnlyList<string> pageTexts, string? title)
        {
            PageCount = pageCount;
            PageSizes = pageSizes;
            PageTexts = pageTexts;
            Title = title;
        }

        public int PageCount { get; }

        /// <summary>各ページの幅と高さ(pt)。</summary>
        public IReadOnlyList<(double Width, double Height)> PageSizes { get; }

        /// <summary>各ページの文字列(単語を空白でつないだもの)。</summary>
        public IReadOnlyList<string> PageTexts { get; }

        public string AllText => string.Join("\n", PageTexts);

        public string? Title { get; }

        public static PdfInspector Read(byte[] pdf)
        {
            using var document = PdfDocument.Open(new MemoryStream(pdf));
            var sizes = new List<(double, double)>();
            var texts = new List<string>();
            foreach (var page in document.GetPages())
            {
                sizes.Add((page.Width, page.Height));
                texts.Add(string.Join(" ", page.GetWords().Select(w => w.Text)));
            }

            return new PdfInspector(document.NumberOfPages, sizes, texts, document.Information.Title);
        }
    }
}
