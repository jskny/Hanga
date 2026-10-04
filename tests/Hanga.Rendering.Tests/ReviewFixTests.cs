using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using static Hanga.Rendering.Tests.ReportRendererWaitTests;

namespace Hanga.Rendering.Tests
{
    /// <summary>コードレビュー・セキュリティレビューの指摘への対応(要件3.4, 3.5, 8.1〜8.4, 9.6)。Chromium を使う。</summary>
    public class ReviewFixTests
    {
        [Fact]
        public async Task Chromiumでの想定外の失敗は段階付きのHanga例外にする()
        {
            // 要件8.1〜8.3: 完了条件の式の書き間違いなど、PuppeteerSharp の例外をそのまま出さない
            var ex = await Assert.ThrowsAsync<HangaBrowserException>(() =>
                RenderAsync(Page("<p>本文</p>"), new FakeVirtualOriginHandler(), o => o.ReadyExpression = "window.(("));
            Assert.Equal(HangaStage.PageLoad, ex.Stage);
            Assert.Contains("Chrome/", ex.BrowserVersion);
            Assert.NotNull(ex.InnerException);
        }

        [Fact]
        public async Task dataのURLは外部への要求として遮断しない()
        {
            const string Pixel = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
            var result = await RenderAsync(Page("<p>画像</p><img src='" + Pixel + "'>"), new FakeVirtualOriginHandler(), o => o.Strict = true);
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public async Task 許可した外部ホストへの要求の失敗はエラー()
        {
            // 要件3.5: 許可したホストから必要なファイルが取れなかった PDF も返さない(.invalid は名前解決できない)
            var ex = await Assert.ThrowsAsync<HangaResourceRequestException>(() => RenderAsync(
                Page("<script src='https://allowed.invalid/lib.js'></script>"),
                new FakeVirtualOriginHandler(),
                configureGlobal: g => g.AllowedExternalHosts.Add("allowed.invalid")));
            Assert.Contains(ex.FailedRequests, f => f.Url == "https://allowed.invalid/lib.js");
        }

        [Fact]
        public async Task 帳票ごとの応答の合計が上限を超えればエラー()
        {
            var handler = new FakeVirtualOriginHandler()
                .Text("/css/a.css", "text/css", new string(' ', 600))
                .Text("/css/b.css", "text/css", new string(' ', 600));
            var ex = await Assert.ThrowsAsync<HangaResourceRequestException>(() => RenderAsync(
                Page("<link rel='stylesheet' href='/css/a.css'><link rel='stylesheet' href='/css/b.css'><p>本文</p>"),
                handler,
                configureGlobal: g => g.MaxTotalResponseBytesPerReport = 1000));
            Assert.Single(ex.FailedRequests);
        }

        [Fact]
        public async Task 時間切れの後はアプリへの転送を取り消す()
        {
            // 要件9.6: 時間切れの後も、オペレーターの権限での API の処理が続かないようにする
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new FakeVirtualOriginHandler().OnCancellable("/api/slow", async (_, token) =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult(true);
                    throw;
                }

                return new VirtualResponse(200, "application/json", Encoding.UTF8.GetBytes("{}"));
            });

            await Assert.ThrowsAsync<HangaTimeoutException>(() => RenderAsync(
                Page("<script>fetch('/api/slow');</script>"), handler, o => o.Timeout = TimeSpan.FromSeconds(2)));
            Assert.Same(cancelled.Task, await Task.WhenAny(cancelled.Task, Task.Delay(TimeSpan.FromSeconds(10))));
        }

        [Fact]
        public void JavaScriptの文字列は改行と引用符と行区切りを逃がす()
        {
            Assert.Equal("'a\\'b\\\\c\\nd\\u2028e\\u003C/script>'", ReportPage.JsString("a'b\\c\nd\u2028e</script>"));
        }
    }
}
