using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hanga.Rendering.Tests
{
    /// <summary>ページの表示と要求の振り分け(要件2.3, 2.4, 3.1, 3.4〜3.7)。Chromium を使う。</summary>
    public class ReportPageTests
    {
        private const string Origin = "https://hanga.invalid";

        [Fact]
        public async Task 帳票のHTMLを仮想オリジンで表示しCSSとJavaScriptを振り分け先から読む()
        {
            var handler = new FakeVirtualOriginHandler()
                .Text("/css/site.css", "text/css", "h1 { color: rgb(1, 2, 3); }")
                .Text("/js/site.js", "text/javascript", "document.getElementById('js').textContent = 'JS が実行された';");
            string html = "<html><head><link rel='stylesheet' href='/css/site.css?v=abc'></head><body><h1>見出し</h1><p id='js'></p><script src='/js/site.js'></script></body></html>";

            await RunAsync(html, handler, async page =>
            {
                Assert.Equal(Origin + "/__hanga/report", page.Page.Url);
                Assert.Equal("rgb(1, 2, 3)", await page.Page.EvaluateExpressionAsync<string>("getComputedStyle(document.querySelector('h1')).color"));
                Assert.Equal("JS が実行された", await page.Page.EvaluateExpressionAsync<string>("document.getElementById('js').textContent"));
                Assert.Empty(page.FailedRequests);
                Assert.Empty(page.Warnings);
            });

            var css = handler.Received.Single(r => r.Path == "/css/site.css");
            Assert.Equal("GET", css.Method);
            Assert.Equal("?v=abc", css.QueryString);
        }

        [Fact]
        public async Task 相対パスのfetchは同一オリジンとして振り分け先に届きPOSTの本文も渡る()
        {
            var handler = new FakeVirtualOriginHandler()
                .On("/api/echo", r => new VirtualResponse(200, "application/json", Encoding.UTF8.GetBytes("{\"echo\":\"" + Encoding.UTF8.GetString(r.Body ?? Array.Empty<byte>()) + "\"}")));
            string html = "<html><body><p id='r'>(読み込み中)</p><script>"
                + "fetch('/api/echo', { method: 'POST', body: 'こんにちは' }).then(r => r.json()).then(j => document.getElementById('r').textContent = j.echo);"
                + "</script></body></html>";

            await RunAsync(html, handler, async page =>
            {
                Assert.Equal("こんにちは", await page.Page.EvaluateExpressionAsync<string>("document.getElementById('r').textContent"));
            });

            Assert.Equal("POST", handler.Received.Single(r => r.Path == "/api/echo").Method);
        }

        [Fact]
        public async Task Hanga自身のファイルを返す()
        {
            var handler = new FakeVirtualOriginHandler();
            string html = "<html><body><p id='r'></p><script>fetch('/__hanga/hello.txt').then(r => r.text()).then(t => document.getElementById('r').textContent = t);</script></body></html>";
            var resources = new Dictionary<string, HangaResource> { ["hello.txt"] = new HangaResource("text/plain", Encoding.UTF8.GetBytes("Hanga のファイル")) };

            await RunAsync(html, handler, async page =>
            {
                Assert.Equal("Hanga のファイル", await page.Page.EvaluateExpressionAsync<string>("document.getElementById('r').textContent"));
            }, setup => setup.Resources = resources);

            Assert.Empty(handler.Received); // 振り分け先(アプリ)には渡さない
        }

        [Fact]
        public async Task faviconは振り分け先に渡さず失敗にしない()
        {
            var handler = new FakeVirtualOriginHandler();
            await RunAsync("<html><body>favicon</body></html>", handler, page =>
            {
                Assert.Empty(page.FailedRequests);
                return Task.CompletedTask;
            });

            Assert.DoesNotContain(handler.Received, r => r.Path == "/favicon.ico");
        }

        [Fact]
        public async Task 失敗とリダイレクトの応答を記録する()
        {
            // 要件3.5: ログイン画面への転送(302)も失敗として扱う
            var handler = new FakeVirtualOriginHandler()
                .Text("/api/error", "text/plain", "error", status: 500)
                .Text("/api/login", "text/plain", "", status: 302, headers: new Dictionary<string, string> { ["Location"] = "/Account/Login" });
            string html = "<html><body><script>fetch('/api/error?token=secret'); fetch('/api/login', { redirect: 'manual' });</script></body></html>";

            await RunAsync(html, handler, page =>
            {
                Assert.Contains(page.FailedRequests, f => f.Url == Origin + "/api/error" && f.StatusCode == 500); // クエリは載せない(要件8.5)
                Assert.Contains(page.FailedRequests, f => f.Url == Origin + "/api/login" && f.StatusCode == 302);
                return Task.CompletedTask;
            });
        }

        [Fact]
        public async Task 振り分け先の例外は失敗として記録しページは止まらない()
        {
            var handler = new FakeVirtualOriginHandler().On("/api/throw", _ => throw new InvalidOperationException("振り分け先の例外"));
            string html = "<html><body><p id='r'>(読み込み中)</p><script>fetch('/api/throw').then(r => document.getElementById('r').textContent = 'status ' + r.status);</script></body></html>";

            await RunAsync(html, handler, async page =>
            {
                Assert.Equal("status 500", await page.Page.EvaluateExpressionAsync<string>("document.getElementById('r').textContent"));
                Assert.Contains(page.FailedRequests, f => f.Url == Origin + "/api/throw" && f.StatusCode == 0);
            });
        }

        [Fact]
        public async Task 許可していない外部への要求を遮断して警告しクエリは載せない()
        {
            // 要件3.4, 3.6, 8.5
            var handler = new FakeVirtualOriginHandler();
            string html = "<html><head><script src='https://cdn.example.com/lib.js?token=secret'></script></head><body>外部</body></html>";

            await RunAsync(html, handler, page =>
            {
                var warning = Assert.Single(page.Warnings);
                Assert.Equal(HangaWarningKind.BlockedExternalRequest, warning.Kind);
                Assert.Equal("https://cdn.example.com/lib.js", warning.Detail);
                return Task.CompletedTask;
            });
        }

        [Fact]
        public async Task 許可した外部ホストへの要求は遮断しない()
        {
            var handler = new FakeVirtualOriginHandler();
            string html = "<html><head><script src='https://allowed.invalid/lib.js'></script></head><body>外部</body></html>";

            // .invalid は名前解決できないため取得自体は失敗するが、Hanga は遮断しない(警告を出さない)
            await RunAsync(html, handler, page =>
            {
                Assert.Empty(page.Warnings);
                return Task.CompletedTask;
            }, setup => setup.AllowedExternalHosts = new[] { "allowed.invalid" });
        }

        [Fact]
        public async Task アプリが返すSetCookieはChromiumに渡さない()
        {
            var handler = new FakeVirtualOriginHandler()
                .Text("/js/cookie.js", "text/javascript", "window.loaded = true;", headers: new Dictionary<string, string> { ["Set-Cookie"] = "leak=1; Path=/" });
            string html = "<html><body><script src='/js/cookie.js'></script></body></html>";

            await RunAsync(html, handler, async page =>
            {
                Assert.True(await page.Page.EvaluateExpressionAsync<bool>("window.loaded === true"));
                Assert.Equal(string.Empty, await page.Page.EvaluateExpressionAsync<string>("document.cookie"));
            });
        }

        private static async Task RunAsync(string html, IVirtualOriginHandler handler, Func<ReportPage, Task> assert, Action<ReportPageSetup>? configure = null)
        {
            await using var host = new BrowserHost(BrowserHostTests.Options());
            await using var lease = await host.AcquireAsync();
            var setup = new ReportPageSetup(Origin, html, handler);
            configure?.Invoke(setup);
            var page = await ReportPage.CreateAsync(lease, setup, NullLogger.Instance, default);
            await page.NavigateAsync();
            await assert(page);
        }
    }
}
