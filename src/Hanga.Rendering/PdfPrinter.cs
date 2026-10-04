using System.Threading.Tasks;
using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace Hanga.Rendering
{
    /// <summary>ページを PDF にする(design.md「⑨」)。体裁の詳細はタスク7で実装する。</summary>
    internal static class PdfPrinter
    {
        public static Task<byte[]> PrintAsync(IPage page, Cshtml2PdfOptions options)
        {
            (double width, double height) = options.PageSizeMm;
            return page.PdfDataAsync(new PdfOptions
            {
                Width = width + "mm",
                Height = height + "mm",
                PrintBackground = options.PrintBackground,
                MarginOptions = new MarginOptions
                {
                    Top = options.Margins.Top + "mm",
                    Right = options.Margins.Right + "mm",
                    Bottom = options.Margins.Bottom + "mm",
                    Left = options.Margins.Left + "mm",
                },
            });
        }
    }
}
