using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using Xunit;
using static Hanga.Rendering.Tests.ReportRendererWaitTests;

namespace Hanga.Rendering.Tests
{
    /// <summary>外字・異体字・字形の無い文字(要件6)。Chromium と IPAmj明朝(fonts-ipamj-mincho)を使う。</summary>
    public class GlyphSupportTests
    {
        private const string GaijiFamily = "IPAmj明朝";
        private const string GaijiFile = "/usr/share/fonts/truetype/ipamj/ipamjm.ttf";

        /// <summary>本文は IPAゴシック(「𠮷」と異体字の字形を持たない)。</summary>
        private static string Body(string text) => Page("<p style=\"font-family:'IPAGothic'\">" + text + "</p>");

        [Fact]
        public async Task 外字は外字用フォントで描く()
        {
            // 要件6.1, 6.2: 本文フォントに字形の無い「𠮷」は外字用フォントで、それ以外は本文フォントのまま
            var result = await RenderAsync(Body("𠮷野家 様"), new FakeVirtualOriginHandler(), configureGlobal: g => g.GaijiFontFamily = GaijiFamily);

            Assert.Equal("IPAmj", FontOf(result.Pdf, "𠮷"));
            Assert.Equal("IPAGothic", FontOf(result.Pdf, "野"));
            Assert.DoesNotContain(result.Warnings, w => w.Kind == HangaWarningKind.MissingGlyph);
        }

        [Fact]
        public async Task 異体字は外字用フォントで描く()
        {
            // 要件6.4: 本文フォントに「葛」の字形があっても、異体字セレクタ付きなら外字用フォントで描く
            var result = await RenderAsync(Body("葛\U000E0102城 と 葛城"), new FakeVirtualOriginHandler(), configureGlobal: g => g.GaijiFontFamily = GaijiFamily);

            using var document = PdfDocument.Open(result.Pdf);
            var kudzu = document.GetPage(1).Letters.Where(l => l.Value.StartsWith("葛", StringComparison.Ordinal)).Select(l => FontFamily(l.FontName)).ToList();
            Assert.Equal(new[] { "IPAmj", "IPAGothic" }, kudzu);
        }

        [Fact]
        public async Task 外字用フォントが無ければ字形の無い文字を警告する()
        {
            // 要件6.7: 既定は警告にとどめて PDF を返す
            var result = await RenderAsync(Body("通常の文字 𠮷野家 未割り当て͹"), new FakeVirtualOriginHandler());

            var warning = Assert.Single(result.Warnings);
            Assert.Equal(HangaWarningKind.MissingGlyph, warning.Kind);
            Assert.Contains("U+20BB7", warning.Detail);
            Assert.Contains("U+0379", warning.Detail);
            Assert.DoesNotContain("U+901A", warning.Detail); // 「通」は本文フォントにある
        }

        [Fact]
        public async Task 外字用フォントがあれば外字は字形の無い文字にしない()
        {
            var result = await RenderAsync(Body("𠮷野家 未割り当て͹"), new FakeVirtualOriginHandler(), configureGlobal: g => g.GaijiFontFamily = GaijiFamily);

            var warning = Assert.Single(result.Warnings);
            Assert.DoesNotContain("U+20BB7", warning.Detail);
            Assert.Contains("U+0379", warning.Detail);
        }

        [Fact]
        public async Task scriptとstyleの中の文字は字形の確認の対象にしない()
        {
            var result = await RenderAsync(Page("<p style=\"font-family:'IPAGothic'\">本文</p><script>var s = '͹';</script>"), new FakeVirtualOriginHandler());
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public async Task 厳格な扱いでは字形の無い文字をエラーにする()
        {
            var ex = await Assert.ThrowsAsync<HangaStrictModeException>(() => RenderAsync(Body("未割り当て͹"), new FakeVirtualOriginHandler(), o => o.Strict = true));
            Assert.Equal(HangaWarningKind.MissingGlyph, Assert.Single(ex.Warnings).Kind);
        }

        [Fact]
        public async Task 字形の確認を無効にできる()
        {
            var result = await RenderAsync(Body("未割り当て͹"), new FakeVirtualOriginHandler(), configureGlobal: g => g.DetectMissingGlyphs = false);
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public async Task 外字用フォントがサーバーに無ければ設定の誤りとしてエラー()
        {
            // 要件6.6: 名前の指定は Chromium の起動時に確かめる
            var ex = await Assert.ThrowsAsync<HangaConfigurationException>(() =>
                RenderAsync(Body("本文"), new FakeVirtualOriginHandler(), configureGlobal: g => g.GaijiFontFamily = "存在しない外字フォント"));
            Assert.Contains("存在しない外字フォント", ex.Message);
        }

        [Fact]
        public async Task 外字用フォントをファイルで指定できる()
        {
            Assert.True(File.Exists(GaijiFile), "IPAmj明朝(fonts-ipamj-mincho)をインストールしてください: " + GaijiFile);
            var result = await RenderAsync(Body("𠮷野家"), new FakeVirtualOriginHandler(), o => o.Timeout = TimeSpan.FromSeconds(60), g => g.GaijiFontFile = GaijiFile);
            Assert.Equal("IPAmj", FontOf(result.Pdf, "𠮷"));
        }

        [Fact]
        public void CSSの文字列は引用符と制御文字を逃がす()
        {
            Assert.Equal("'IPAmj明朝'", GlyphSupport.CssString("IPAmj明朝"));
            Assert.Equal("'a\\27 ) b'", GlyphSupport.CssString("a') b"));
        }

        /// <summary>PDF の中で、その文字を描いたフォントの名前(サブセットの接頭辞と書体名の後半を除く)。</summary>
        private static string FontOf(byte[] pdf, string ch)
        {
            using var document = PdfDocument.Open(pdf);
            return FontFamily(document.GetPage(1).Letters.First(l => l.Value == ch).FontName);
        }

        private static string FontFamily(string fontName)
        {
            string name = fontName.Contains('+') ? fontName.Substring(fontName.IndexOf('+') + 1) : fontName;
            return name.StartsWith("IPAmj", StringComparison.Ordinal) ? "IPAmj" : name.StartsWith("IPAGothic", StringComparison.Ordinal) ? "IPAGothic" : name;
        }
    }
}
