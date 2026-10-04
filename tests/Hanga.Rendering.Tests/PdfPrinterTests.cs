using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Hanga.TestSupport;
using UglyToad.PdfPig;
using Xunit;
using static Hanga.Rendering.Tests.ReportRendererWaitTests;

namespace Hanga.Rendering.Tests
{
    /// <summary>体裁と PDF 化(要件2.5, 5.1〜5.9)。Chromium を使う。</summary>
    public class PdfPrinterTests
    {
        /// <summary>ページサイズの許容誤差(pt)。Chromium は内部で丸めるため、正確な寸法にはならない(design.md「⑨」)。</summary>
        private const double Tolerance = 1.0;

        private static double Pt(double mm) => mm / 25.4 * 72;

        [Theory]
        [InlineData(PageOrientation.Portrait, 210, 297)]
        [InlineData(PageOrientation.Landscape, 297, 210)]
        public async Task A4の縦と横のページサイズ(PageOrientation orientation, double widthMm, double heightMm)
        {
            var pdf = await PrintAsync(Page("<p>用紙</p>"), o => o.Orientation = orientation);
            Assert.InRange(pdf.PageSizes[0].Width, Pt(widthMm) - Tolerance, Pt(widthMm) + Tolerance);
            Assert.InRange(pdf.PageSizes[0].Height, Pt(heightMm) - Tolerance, Pt(heightMm) + Tolerance);
        }

        [Fact]
        public async Task 寸法を指定した用紙()
        {
            var pdf = await PrintAsync(Page("<p>はがき</p>"), o => { o.PaperSize = PaperSize.Custom(100, 148); o.Margins = PageMargins.Uniform(5); });
            Assert.InRange(pdf.PageSizes[0].Width, Pt(100) - Tolerance, Pt(100) + Tolerance);
            Assert.InRange(pdf.PageSizes[0].Height, Pt(148) - Tolerance, Pt(148) + Tolerance);
        }

        [Fact]
        public async Task 既定は印刷用のCSSで画面用に切り替えられる()
        {
            string html = Page("<style>.print-only{display:none}@media print{.print-only{display:block}.screen-only{display:none}}</style>"
                + "<p class='screen-only'>画面用の文</p><p class='print-only'>印刷用の文</p>");

            var print = await PrintAsync(html);
            Assert.Contains("印刷用の文", print.AllText);
            Assert.DoesNotContain("画面用の文", print.AllText);

            var screen = await PrintAsync(html, o => o.CssMedia = CssMedia.Screen);
            Assert.Contains("画面用の文", screen.AllText);
            Assert.DoesNotContain("印刷用の文", screen.AllText);
        }

        [Fact]
        public async Task 幅の広い表は既定で縮小して右端の列まで出す()
        {
            // 要件5.4。縮小しないと右端の列が欠ける(design.md「検証結果」)
            string html = WideTable(rows: 20);
            var fit = await PrintAsync(html);
            Assert.Equal(20, Count(fit.AllText, "RIGHT-EDGE"));

            var noFit = await PrintAsync(html, o => o.FitToPageWidth = false);
            Assert.Equal(0, Count(noFit.AllText, "RIGHT-EDGE"));
            Assert.Equal(20, Count(noFit.AllText, "LEFT"));
        }

        [Fact]
        public async Task 用紙の幅に合わせて伸び縮みする画面は縮小しない()
        {
            // 画面の幅を印刷可能な幅にしてから測るため、width:100% の画面は倍率 1 のまま(16px の文字は 12pt)
            byte[] pdf = (await RenderAsync(Page("<div style='width:100%'><p style='font-size:16px'>伸び縮みする画面</p></div>"), new FakeVirtualOriginHandler())).Pdf;
            using var document = PdfDocument.Open(pdf);
            var letter = document.GetPage(1).Letters.First(l => l.Value == "伸");
            Assert.InRange(letter.PointSize, 11.5, 12.5);
        }

        [Fact]
        public async Task 全体を1ページにする()
        {
            // 要件5.5
            string html = WideTable(rows: 150);
            var normal = await PrintAsync(html);
            Assert.True(normal.PageCount > 1);

            var single = await PrintAsync(html, o => o.SinglePage = true);
            Assert.Equal(1, single.PageCount);
            Assert.Equal(150, Count(single.AllText, "RIGHT-EDGE"));
        }

        [Fact]
        public async Task ページ番号をフッターに出す()
        {
            // 要件5.8
            var builder = new StringBuilder();
            for (int i = 1; i <= 80; i++)
            {
                builder.Append("<p>行").Append(i).Append("</p>");
            }

            var pdf = await PrintAsync(Page(builder.ToString()), o => o.PageNumbers = true);
            Assert.True(pdf.PageCount >= 2);
            Assert.Contains($"1 / {pdf.PageCount}", pdf.PageTexts[0]);
            Assert.Contains($"{pdf.PageCount} / {pdf.PageCount}", pdf.PageTexts[pdf.PageCount - 1]);
        }

        [Fact]
        public async Task 文書のタイトルは指定が無ければページのtitle()
        {
            // 要件5.9
            string html = "<!DOCTYPE html><html><head><meta charset='utf-8'><title>ページのタイトル</title></head><body>本文</body></html>";
            Assert.Equal("ページのタイトル", (await PrintAsync(html)).Title);
            Assert.Equal("指定したタイトル", (await PrintAsync(html, o => o.Title = "指定したタイトル")).Title);
        }

        [Fact]
        public async Task 日本語の文字列を取り出せる()
        {
            // 要件2.5: 画像化せず、文字として出力する
            var pdf = await PrintAsync(Page("<p>請求書 株式会社サンプル 御中</p>"));
            Assert.Contains("株式会社サンプル", pdf.AllText);
        }

        private static async Task<PdfInspector> PrintAsync(string html, Action<Cshtml2PdfOptions>? configure = null) =>
            PdfInspector.Read((await RenderAsync(html, new FakeVirtualOriginHandler(), configure)).Pdf);

        private static string WideTable(int rows)
        {
            var builder = new StringBuilder("<style>table{border-collapse:collapse;width:1800px}td{border:1px solid #333}</style><table>");
            for (int r = 1; r <= rows; r++)
            {
                builder.Append("<tr><td>LEFT").Append(r).Append("</td><td style='width:1500px'>中央</td><td>RIGHT-EDGE").Append(r).Append("</td></tr>");
            }

            return Page(builder.Append("</table>").ToString());
        }

        private static int Count(string text, string word)
        {
            int count = 0;
            for (int i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + word.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }
    }
}
