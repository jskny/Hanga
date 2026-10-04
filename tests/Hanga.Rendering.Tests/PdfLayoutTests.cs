using System.Globalization;
using System.Threading;
using Xunit;

namespace Hanga.Rendering.Tests
{
    /// <summary>体裁の計算(Chromium を使わない部分。要件5.1〜5.5)。</summary>
    public class PdfLayoutTests
    {
        [Fact]
        public void 長さの換算()
        {
            Assert.Equal(96, PdfLayout.MmToPx(25.4), 6);
            Assert.Equal(25.4, PdfLayout.PxToMm(96), 6);
        }

        [Fact]
        public void 印刷可能な幅は用紙の幅から左右の余白を引いたもの()
        {
            var options = new Cshtml2PdfOptions(); // A4 縦、余白 10mm
            Assert.Equal(PdfLayout.MmToPx(190), PdfLayout.PrintableWidthPx(options), 6);
            options.Orientation = PageOrientation.Landscape;
            Assert.Equal(PdfLayout.MmToPx(277), PdfLayout.PrintableWidthPx(options), 6);
        }

        [Fact]
        public void 収まる内容は指定の倍率のまま()
        {
            var options = new Cshtml2PdfOptions();
            Assert.Equal(1.0, PdfLayout.ComputeScale(options, 700));
            options.Scale = 0.8;
            Assert.Equal(0.8, PdfLayout.ComputeScale(options, 700));
        }

        [Fact]
        public void はみ出す内容は印刷可能な幅に収まるまで縮小する()
        {
            var options = new Cshtml2PdfOptions();
            double printable = PdfLayout.PrintableWidthPx(options);
            Assert.Equal(printable / 1800, PdfLayout.ComputeScale(options, 1800), 6);

            // 指定の倍率を掛けた後にはみ出す場合も、収まるまで縮小する
            options.Scale = 1.5;
            Assert.Equal(printable / 500, PdfLayout.ComputeScale(options, 500), 6);
        }

        [Fact]
        public void 幅を収める指定が無効ならはみ出しても縮小しない()
        {
            var options = new Cshtml2PdfOptions { FitToPageWidth = false };
            Assert.Equal(1.0, PdfLayout.ComputeScale(options, 1800));
        }

        [Fact]
        public void 倍率はChromiumの範囲に丸める()
        {
            var options = new Cshtml2PdfOptions();
            Assert.Equal(Cshtml2PdfOptions.MinScale, PdfLayout.ComputeScale(options, 1_000_000));
            Assert.Equal(1.0, PdfLayout.ComputeScale(options, 0));
        }

        [Fact]
        public void 一ページにする場合の高さ()
        {
            var options = new Cshtml2PdfOptions(); // 上下の余白 10mm
            double expected = PdfLayout.PxToMm(1000 * 0.5) + 20 + PdfLayout.SinglePageSlackMm;
            Assert.Equal(expected, PdfLayout.SinglePageHeightMm(options, 1000, 0.5), 6);
            Assert.Equal(PaperSize.MaxMillimeters, PdfLayout.SinglePageHeightMm(options, 10_000_000, 1));
        }

        [Fact]
        public void 長さの文字列は文化圏によらず小数点がピリオド()
        {
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal("215.9mm", PdfLayout.Mm(215.9));
                Assert.Equal("210mm", PdfLayout.Mm(210));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }
    }
}
