using System;
using System.Threading.Tasks;
using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace Hanga.Rendering
{
    /// <summary>体裁を決めてページを PDF にする(design.md「⑨」、要件5)。</summary>
    internal static class PdfPrinter
    {
        /// <summary>ページ番号のフッター(要件5.8)。Chromium が <c>pageNumber</c>・<c>totalPages</c> の中身を埋める。</summary>
        internal const string PageNumberFooter =
            "<div style=\"font-size:9px;width:100%;text-align:center;\"><span class=\"pageNumber\"></span> / <span class=\"totalPages\"></span></div>";

        public static async Task<byte[]> PrintAsync(IPage page, Cshtml2PdfOptions options)
        {
            // 1. 印刷用/画面用の CSS(要件5.6)
            await page.EmulateMediaTypeAsync(options.CssMedia == CssMedia.Screen ? MediaType.Screen : MediaType.Print).ConfigureAwait(false);

            // 2. 画面の幅を印刷可能な幅にしてから内容の幅を測る。用紙の幅に合わせて伸び縮みする画面を、不要に縮小しないため(要件5.4)
            double printableWidthPx = PdfLayout.PrintableWidthPx(options);
            await SetViewportWidthAsync(page, printableWidthPx).ConfigureAwait(false);
            double contentWidth = await page.EvaluateFunctionAsync<double>(
                "() => Math.max(document.documentElement.scrollWidth, document.body ? document.body.scrollWidth : 0)").ConfigureAwait(false);
            double scale = PdfLayout.ComputeScale(options, contentWidth);

            (double widthMm, double heightMm) = options.PageSizeMm;
            if (options.SinglePage)
            {
                // 縮小して印刷すると、内容は「印刷可能な幅 ÷ 倍率」の幅で組まれる。その幅で高さを測る(要件5.5)
                await SetViewportWidthAsync(page, printableWidthPx / scale).ConfigureAwait(false);
                double contentHeight = await page.EvaluateFunctionAsync<double>(
                    "() => Math.max(document.documentElement.scrollHeight, document.body ? document.body.scrollHeight : 0)").ConfigureAwait(false);
                heightMm = PdfLayout.SinglePageHeightMm(options, contentHeight, scale);
            }

            // 3. 文書のタイトル(要件5.9)。Chromium は document.title を PDF の文書のタイトルにする。値は引数で渡し、式に埋め込まない
            if (options.Title != null)
            {
                await page.EvaluateFunctionAsync("t => { document.title = t; }", options.Title).ConfigureAwait(false);
            }

            // 4. PDF。用紙は PuppeteerSharp の PaperFormat を使わず mm で指定する(要件5.2)
            var pdfOptions = new PdfOptions
            {
                Width = PdfLayout.Mm(widthMm),
                Height = PdfLayout.Mm(heightMm),
                Scale = (decimal)Math.Round(scale, 4),
                PrintBackground = options.PrintBackground,
                MarginOptions = new MarginOptions
                {
                    Top = PdfLayout.Mm(options.Margins.Top),
                    Right = PdfLayout.Mm(options.Margins.Right),
                    Bottom = PdfLayout.Mm(options.Margins.Bottom),
                    Left = PdfLayout.Mm(options.Margins.Left),
                },
            };
            if (options.PageNumbers)
            {
                pdfOptions.DisplayHeaderFooter = true;
                pdfOptions.HeaderTemplate = "<span></span>";
                pdfOptions.FooterTemplate = PageNumberFooter;
            }

            return await page.PdfDataAsync(pdfOptions).ConfigureAwait(false);
        }

        private static Task SetViewportWidthAsync(IPage page, double widthPx) =>
            page.SetViewportAsync(new ViewPortOptions { Width = Math.Max(1, (int)Math.Round(widthPx)), Height = 800 });
    }
}
