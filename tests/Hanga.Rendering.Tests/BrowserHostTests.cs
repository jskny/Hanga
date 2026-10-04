using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Hanga.TestSupport;
using PuppeteerSharp;
using Xunit;

namespace Hanga.Rendering.Tests
{
    /// <summary>共有の Chromium の管理(要件9.2, 9.4〜9.6, 10.1, 10.2, 11.3)。Chromium を使う。</summary>
    public class BrowserHostTests
    {
        internal static HangaOptions Options(int maxConcurrent = 4) => new HangaOptions
        {
            ChromiumExecutablePath = TestChromium.ExecutablePath,
            ChromiumArguments = TestChromium.Arguments.ToList(),
            MaxConcurrentRenders = maxConcurrent,
        };

        [Fact]
        public async Task 最初の利用時に起動して版を記録する()
        {
            await using var host = new BrowserHost(Options());
            Assert.Null(host.BrowserVersion);

            await using (var lease = await host.AcquireAsync())
            {
                Assert.Contains("Chrome/", lease.BrowserVersion);
            }

            await using (await host.AcquireAsync())
            {
            }

            Assert.Equal(1, host.LaunchCount); // 2 件目は起動し直さない(使い回す。要件10.1)
            Assert.Contains("Chrome/", host.BrowserVersion);
        }

        [Fact]
        public async Task 同時に要求しても起動は1回だけ()
        {
            await using var host = new BrowserHost(Options(maxConcurrent: 8));
            var leases = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => host.AcquireAsync()));
            foreach (var lease in leases)
            {
                await lease.DisposeAsync();
            }

            Assert.Equal(1, host.LaunchCount);
        }

        [Fact]
        public async Task 実行ファイルが無ければ場所を含むエラー()
        {
            var options = Options();
            options.ChromiumExecutablePath = "/nonexistent/chrome";
            await using var host = new BrowserHost(options);
            var ex = await Assert.ThrowsAsync<HangaBrowserException>(() => host.AcquireAsync());
            Assert.Equal("/nonexistent/chrome", ex.ExecutablePath);
            Assert.Equal(HangaStage.BrowserLaunch, ex.Stage);

            // 枠は返されている(次の要求も同じエラーになり、待ち続けない)
            await Assert.ThrowsAsync<HangaBrowserException>(() => host.AcquireAsync());
        }

        [Fact]
        public async Task プロセスが終了していたら起動し直す()
        {
            // 要件9.5
            await using var host = new BrowserHost(Options());
            await using (await host.AcquireAsync())
            {
            }

            var process = host.CurrentBrowser!.Process!;
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10000);
            for (int i = 0; i < 50 && host.CurrentBrowser!.IsConnected; i++)
            {
                await Task.Delay(100);
            }

            await using (var lease = await host.AcquireAsync())
            {
                var page = await lease.Context.NewPageAsync();
                Assert.Equal(2, await page.EvaluateExpressionAsync<int>("1 + 1"));
            }

            Assert.Equal(2, host.LaunchCount);
        }

        [Fact]
        public async Task 枠を超えた要求は待たされる()
        {
            // 要件9.4
            await using var host = new BrowserHost(Options(maxConcurrent: 1));
            var first = await host.AcquireAsync();
            var second = host.AcquireAsync();

            await Task.Delay(500);
            Assert.False(second.IsCompleted);

            await first.DisposeAsync();
            Assert.Same(second, await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(10)))); // .NET 5 には Task.WaitAsync が無い
            await using var lease = await second;
            Assert.NotNull(lease.Context);
        }

        [Fact]
        public async Task 待っている間に取り消せる()
        {
            // 要件9.6
            await using var host = new BrowserHost(Options(maxConcurrent: 1));
            await using var first = await host.AcquireAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.AcquireAsync(cts.Token));
        }

        [Fact]
        public async Task 帳票ごとのコンテキストでlocalStorageを共有しない()
        {
            // 要件9.2: 仮想オリジンは全帳票で共通のため、コンテキストを分けないと別の帳票から値が見える(design.md「検証結果」)
            await using var host = new BrowserHost(Options());
            await using (var a = await host.AcquireAsync())
            {
                var page = await OpenOnOriginAsync(a.Context);
                await page.EvaluateExpressionAsync("localStorage.setItem('secret', 'operator-A')");
            }

            await using (var b = await host.AcquireAsync())
            {
                var page = await OpenOnOriginAsync(b.Context);
                Assert.Null(await page.EvaluateExpressionAsync<string?>("localStorage.getItem('secret')"));
            }
        }

        /// <summary>仮想オリジン(https://hanga.invalid)のページを開く。localStorage はオリジンを持つページでしか使えないため。</summary>
        private static async Task<IPage> OpenOnOriginAsync(IBrowserContext context)
        {
            var page = await context.NewPageAsync();
            await page.SetRequestInterceptionAsync(true);
            page.Request += async (s, e) => await e.Request.RespondAsync(new ResponseData { Status = HttpStatusCode.OK, ContentType = "text/html", Body = "<html><body>ok</body></html>" });
            var navigation = page.WaitForNavigationAsync();
            await page.EvaluateExpressionAsync("location.href = 'https://hanga.invalid/'");
            await navigation;
            return page;
        }
    }
}
