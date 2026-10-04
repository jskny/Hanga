using System;
using System.Linq;
using Xunit;

namespace Hanga.Core.Tests
{
    public class PaperSizeAndExceptionTests
    {
        [Theory]
        [InlineData("A3", 297, 420)]
        [InlineData("a4", 210, 297)]
        [InlineData("A5", 148, 210)]
        [InlineData("B4", 257, 364)]
        [InlineData("B5", 182, 257)]
        [InlineData("Letter", 215.9, 279.4)]
        [InlineData("LEGAL", 215.9, 355.6)]
        public void 名前から用紙の寸法を得る(string name, double width, double height)
        {
            var paper = PaperSize.FromName(name);
            Assert.Equal(width, paper.WidthMm);
            Assert.Equal(height, paper.HeightMm);
        }

        [Fact]
        public void 知らない用紙の名前はエラー()
        {
            Assert.Throws<HangaConfigurationException>(() => PaperSize.FromName("A10"));
        }

        [Fact]
        public void 寸法を指定した用紙()
        {
            var paper = PaperSize.Custom(100, 148);
            Assert.Equal("Custom", paper.Name);
            Assert.Equal(PaperSize.Custom(100, 148), paper);
        }

        [Theory]
        [InlineData(0, 100)]
        [InlineData(100, -1)]
        [InlineData(100, 5001)]
        [InlineData(double.NaN, 100)]
        public void 寸法が範囲外ならエラー(double width, double height)
        {
            Assert.Throws<HangaConfigurationException>(() => PaperSize.Custom(width, height));
        }

        [Fact]
        public void 例外は失敗した段階を持つ()
        {
            Assert.Equal(HangaStage.ViewRendering, new HangaViewNotFoundException("Order", new[] { "/Views/Home/Order.cshtml" }).Stage);
            Assert.Equal(HangaStage.ViewRendering, new HangaViewRenderingException("Order", new InvalidOperationException("x")).Stage);
            Assert.Equal(HangaStage.ResourceRequest, new HangaResourceRequestException(new[] { new FailedRequest("GET", "https://hanga.invalid/api", 500) }).Stage);
            Assert.Equal(HangaStage.Waiting, new HangaTimeoutException(TimeSpan.FromSeconds(1), "ネットワークの静止", Array.Empty<string>()).Stage);
            Assert.Equal(HangaStage.BrowserLaunch, new HangaBrowserException("起動できません", "/x/chrome", null, HangaStage.BrowserLaunch).Stage);
        }

        [Fact]
        public void ビューが見つからない例外は探した場所を含む()
        {
            var ex = new HangaViewNotFoundException("Order", new[] { "/Views/Home/Order.cshtml", "/Views/Shared/Order.cshtml" });
            Assert.Contains("/Views/Shared/Order.cshtml", ex.Message);
            Assert.Equal(2, ex.SearchedLocations.Count);
        }

        [Fact]
        public void 要求の失敗の例外はURLと状態コードを含む()
        {
            var ex = new HangaResourceRequestException(new[] { new FailedRequest("GET", "https://hanga.invalid/api/orders/1", 302) });
            Assert.Contains("https://hanga.invalid/api/orders/1", ex.Message);
            Assert.Contains("302", ex.Message);
        }

        [Fact]
        public void Chromiumの例外は場所と版を含む()
        {
            var ex = new HangaBrowserException("起動できません", "/x/chrome", null, HangaStage.BrowserLaunch);
            Assert.Contains("/x/chrome", ex.Message);
            Assert.Contains("不明", ex.Message);
        }

        [Fact]
        public void 厳格な扱いの例外は警告の一覧を持つ()
        {
            var warnings = new[] { new HangaWarning(HangaWarningKind.MissingGlyph, "字形の無い文字があります", "U+20BB7") };
            var ex = new HangaStrictModeException(warnings);
            Assert.Single(ex.Warnings);
            Assert.Contains("字形の無い文字", ex.Message);
            Assert.Equal(HangaStage.PageLoad, ex.Stage);
            Assert.Equal(HangaStage.ResourceRequest, new HangaStrictModeException(new[] { new HangaWarning(HangaWarningKind.BlockedExternalRequest, "遮断") }).Stage);
            Assert.Equal("[MissingGlyph] 字形の無い文字があります (U+20BB7)", warnings.Single().ToString());
        }
    }
}
