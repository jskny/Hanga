using System.Linq;
using System.Threading.Tasks;
using Hanga.TestSupport;
using Xunit;

namespace Hanga.Rendering.Tests
{
    /// <summary>
    /// エッジケースの検証(samples/Hanga.EdgeCases)で見つかった問題の回帰テスト(view-pdf-generation の要件2.7, 4.6, 5.5)。Chromium を使う。
    /// </summary>
    public class EdgeCaseTests
    {
        [Theory]
        [InlineData("alert('お知らせ'); document.getElementById('v').textContent = 'ダイアログの後';")]
        [InlineData("document.getElementById('v').textContent = 'ダイアログの後 ' + confirm('よろしいですか');")]
        [InlineData("document.getElementById('v').textContent = 'ダイアログの後 ' + prompt('名前', '既定値');")]
        public async Task ダイアログで止まらずOKで閉じて警告にする(string script)
        {
            // 要件4.6: 閉じないとページの JavaScript が止まり、時間切れになっていた
            var result = await ReportRendererWaitTests.RenderAsync(
                ReportRendererWaitTests.Page("<p id='v'>ダイアログの前</p><script>" + script + "</script>"), new FakeVirtualOriginHandler());

            string text = PdfInspector.Read(result.Pdf).AllText;
            Assert.Contains("ダイアログの後", text);
            Assert.DoesNotContain("false", text); // confirm は OK(true)、prompt は既定値
            Assert.Contains(result.Warnings, w => w.Kind == HangaWarningKind.Dialog);
        }

        [Fact]
        public async Task ダイアログは厳格な扱いではエラーにする()
        {
            await Assert.ThrowsAsync<HangaStrictModeException>(() => ReportRendererWaitTests.RenderAsync(
                ReportRendererWaitTests.Page("<p>本文</p><script>alert('x');</script>"), new FakeVirtualOriginHandler(), o => o.Strict = true));
        }

        [Fact]
        public async Task 開いた後のページの移動を止めて元のページをPDFにする()
        {
            // 要件2.7: 止めないと、移動先(ここではアプリの別のページ)の内容の PDF を黙って返していた
            var handler = new FakeVirtualOriginHandler().Text("/other", "text/html; charset=utf-8", "<p>移動先のページ</p>");
            var result = await ReportRendererWaitTests.RenderAsync(
                ReportRendererWaitTests.Page("<p>元のページ</p><script>setTimeout(function () { location.href = '/other'; }, 100);</script>"), handler);

            string text = PdfInspector.Read(result.Pdf).AllText;
            Assert.Contains("元のページ", text);
            Assert.DoesNotContain("移動先のページ", text);
            var warning = Assert.Single(result.Warnings, w => w.Kind == HangaWarningKind.BlockedNavigation);
            Assert.EndsWith("/other", warning.Detail);
        }

        [Fact]
        public async Task 内側のフレームの移動は止めない()
        {
            var handler = new FakeVirtualOriginHandler().Text("/frame", "text/html; charset=utf-8", "<p>フレームの中</p>");
            var result = await ReportRendererWaitTests.RenderAsync(
                ReportRendererWaitTests.Page("<p>元のページ</p><iframe src='/frame'></iframe>"), handler);

            Assert.DoesNotContain(result.Warnings, w => w.Kind == HangaWarningKind.BlockedNavigation);
        }

        [Fact]
        public async Task 一ページ化で用紙の高さの上限を超えたら警告にする()
        {
            // 要件5.5: 上限(5000mm)を超えた分は次のページになる。黙って複数ページにしていた
            string rows = string.Concat(Enumerable.Range(1, 2500).Select(i => "<div style='height:24px'>行 " + i + "</div>"));
            var result = await ReportRendererWaitTests.RenderAsync(
                ReportRendererWaitTests.Page(rows + "<p>終わり</p>"), new FakeVirtualOriginHandler(), o => o.SinglePage = true);

            var pdf = PdfInspector.Read(result.Pdf);
            Assert.True(pdf.PageCount > 1);
            Assert.Contains("終わり", pdf.AllText);
            Assert.Contains(result.Warnings, w => w.Kind == HangaWarningKind.SinglePageOverflow);
        }
    }
}
