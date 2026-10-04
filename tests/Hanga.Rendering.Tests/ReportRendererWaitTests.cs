using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Hanga.TestSupport;
using Xunit;

namespace Hanga.Rendering.Tests
{
    /// <summary>表示の完了の待機と、エラー・警告の扱い(要件3.5, 4.1〜4.5, 8.4, 8.7)。Chromium を使う。</summary>
    public class ReportRendererWaitTests
    {
        [Fact]
        public async Task 遅れて返るAPIの値を待ってからPDFにする()
        {
            // 要件4.1: 非同期の要求が終わる(ネットワークが静止する)まで待つ
            var handler = new FakeVirtualOriginHandler().Delayed("/api/value", TimeSpan.FromSeconds(1.5), "application/json", "{\"v\":\"遅れて届いた値\"}");
            string html = Page("<p id='v'>(読み込み中)</p><script>fetch('/api/value').then(r => r.json()).then(j => document.getElementById('v').textContent = j.v);</script>");

            var result = await RenderAsync(html, handler);

            Assert.Contains("遅れて届いた値", PdfInspector.Read(result.Pdf).AllText);
        }

        [Fact]
        public async Task 完了条件の式が真になるまで待つ()
        {
            // 要件4.2: 通信を伴わない遅れた描画(タイマー)は、ネットワークの静止では待てないため、完了条件の式で待つ
            string html = Page("<p id='v'>(読み込み中)</p><script>setTimeout(function () { document.getElementById('v').textContent = 'タイマーの値'; window.reportReady = true; }, 1500);</script>");

            var result = await RenderAsync(html, new FakeVirtualOriginHandler(), o => o.ReadyExpression = "window.reportReady === true");

            Assert.Contains("タイマーの値", PdfInspector.Read(result.Pdf).AllText);
        }

        [Fact]
        public async Task 完了条件が満たされなければ条件を含むタイムアウトのエラー()
        {
            // 要件4.3, 4.4
            var stopwatch = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<HangaTimeoutException>(() => RenderAsync(
                Page("<p>完了しない</p>"),
                new FakeVirtualOriginHandler(),
                o => { o.ReadyExpression = "window.neverReady === true"; o.Timeout = TimeSpan.FromSeconds(2); }));

            Assert.Contains("window.neverReady === true", ex.Message);
            Assert.Equal(HangaStage.Waiting, ex.Stage);
            Assert.InRange(stopwatch.Elapsed.TotalSeconds, 1.5, 10);
        }

        [Fact]
        public async Task 遅すぎるAPIは完了していない要求を含むタイムアウトのエラー()
        {
            var handler = new FakeVirtualOriginHandler().Delayed("/api/slow", TimeSpan.FromSeconds(10), "application/json", "{}");
            var ex = await Assert.ThrowsAsync<HangaTimeoutException>(() => RenderAsync(
                Page("<script>fetch('/api/slow?id=1');</script>"),
                handler,
                o => o.Timeout = TimeSpan.FromSeconds(2)));

            Assert.Contains("https://hanga.invalid/api/slow", ex.PendingRequestUrls);
            Assert.DoesNotContain(ex.PendingRequestUrls, u => u.Contains("id=1")); // クエリは載せない(要件8.5)
        }

        [Fact]
        public async Task スクリプトの例外は既定では警告にしてPDFを返す()
        {
            // 要件4.5
            var result = await RenderAsync(Page("<p>本文</p><script>throw new Error('画面の不具合');</script>"), new FakeVirtualOriginHandler());

            var warning = Assert.Single(result.Warnings);
            Assert.Equal(HangaWarningKind.ScriptError, warning.Kind);
            Assert.Contains("画面の不具合", warning.Detail);
            Assert.Contains("本文", PdfInspector.Read(result.Pdf).AllText);
        }

        [Fact]
        public async Task 厳格な扱いではスクリプトの例外と外部への要求の遮断をエラーにする()
        {
            // 要件8.7
            var ex = await Assert.ThrowsAsync<HangaStrictModeException>(() => RenderAsync(
                Page("<script src='https://cdn.example.com/x.js'></script><script>throw new Error('画面の不具合');</script>"),
                new FakeVirtualOriginHandler(),
                o => o.Strict = true));

            Assert.Contains(ex.Warnings, w => w.Kind == HangaWarningKind.ScriptError);
            Assert.Contains(ex.Warnings, w => w.Kind == HangaWarningKind.BlockedExternalRequest);
        }

        [Fact]
        public async Task 失敗した要求があれば設定によらずエラー()
        {
            // 要件3.5, 8.4: 厳格でなくても、必要な値が欠けた PDF は返さない
            var handler = new FakeVirtualOriginHandler().Text("/api/orders/1", "text/plain", "", status: 302);
            var ex = await Assert.ThrowsAsync<HangaResourceRequestException>(() => RenderAsync(
                Page("<script>fetch('/api/orders/1', { redirect: 'manual' });</script>"),
                handler,
                o => o.Strict = false));

            var failed = Assert.Single(ex.FailedRequests);
            Assert.Equal(302, failed.StatusCode);
        }

        internal static string Page(string body) =>
            "<!DOCTYPE html><html><head><meta charset='utf-8'><style>body { font-family: 'IPAGothic', sans-serif; }</style></head><body>" + body + "</body></html>";

        internal static async Task<ReportRenderResult> RenderAsync(string html, IVirtualOriginHandler handler, Action<Cshtml2PdfOptions>? configure = null, Action<HangaOptions>? configureGlobal = null)
        {
            var global = BrowserHostTests.Options();
            configureGlobal?.Invoke(global);
            var glyphs = new GlyphSupport(global);
            await using var host = new BrowserHost(global, afterLaunch: glyphs.VerifyGaijiFontAsync);
            var options = new Cshtml2PdfOptions();
            configure?.Invoke(options);
            return await new ReportRenderer(host, global, glyphs).RenderAsync(new ReportRenderInput(html, options, handler));
        }
    }
}
