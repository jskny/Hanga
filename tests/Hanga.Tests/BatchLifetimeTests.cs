using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hanga.Rendering;
using Hanga.TestReports;
using Hanga.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Hanga.Tests
{
    /// <summary>
    /// バッチ用の変換器の起動・終了・設定(batch-pdf-generation の要件1, 3.2, 3.3, 5.3)。テストごとに <see cref="HangaBatch"/> を起動する。
    /// </summary>
    public class BatchLifetimeTests
    {
        private readonly ITestOutputHelper output;

        public BatchLifetimeTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public async Task 発行した形のWebルートから帳票ライブラリの静的ファイルを読む()
        {
            // 要件3.2: 発行したバッチでは、帳票ライブラリの静的ファイルが wwwroot/_content/<ライブラリ名>/ に置かれる
            using var dir = new BatchTests.TemporaryDirectory();
            string assets = Path.Combine(dir.Path, "wwwroot", "_content", "Hanga.TestReports");
            CopyDirectory(HangaBatchFixture.TestReportsWwwroot, assets);

            await using var batch = await HangaBatch.StartAsync(HangaBatchFixture.NewOptions(), b =>
            {
                b.ViewAssemblies.Add(typeof(InvoiceModel).Assembly);
                b.ContentRootPath = dir.Path;
            });
            var inspector = PdfInspector.Read(await new Cshtml2Pdf(batch, "Invoice", "Invoice", HangaBatchFixture.Invoice("発行")).ToBytesAsync());
            Assert.Contains("(CSS適用)", inspector.AllText);
            Assert.Contains("JS実行済み", inspector.AllText);
        }

        [Fact]
        public async Task 終了するとChromiumを終了し以後は使えない()
        {
            // 要件1.4
            var batch = await HangaBatch.StartAsync(HangaBatchFixture.NewOptions(), HangaBatchFixture.ConfigureTestReports);
            var pdf = new Cshtml2Pdf(batch, "Invoice", "Invoice", HangaBatchFixture.Invoice("終了"));
            Process process = batch.Services.GetRequiredService<BrowserHost>().CurrentBrowser!.Process!;

            await batch.DisposeAsync();
            await batch.DisposeAsync(); // 2 回目は何もしない

            Assert.True(process.WaitForExit(10_000));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => pdf.ToBytesAsync());
            Assert.Throws<ObjectDisposedException>(() => new Cshtml2Pdf(batch, "Invoice", "Invoice"));
        }

        [Fact]
        public async Task 静的ファイルのフォルダが無ければ起動時に知らせる()
        {
            // 要件1.3, 3.3: Chromium を起動する前に、すべての誤りを知らせる
            var ex = await Assert.ThrowsAsync<HangaConfigurationException>(() => HangaBatch.StartAsync(HangaBatchFixture.NewOptions(), b =>
            {
                b.WebRootPath = "no-such-webroot";
                b.StaticFileMappings["_content/Reports"] = HangaBatchFixture.TestReportsWwwroot;
                b.StaticFileMappings["/_content/Other"] = "no-such-folder";
            }));
            Assert.Contains("WebRootPath", ex.Message);
            Assert.Contains("/ で始めて", ex.Message);
            Assert.Contains("no-such-folder", ex.Message);
        }

        [Fact]
        public async Task アプリ全体の設定の誤りとChromiumの起動の失敗を起動時に知らせる()
        {
            // 要件1.2, 1.3
            await Assert.ThrowsAsync<HangaConfigurationException>(() => HangaBatch.StartAsync(new HangaOptions()));

            var options = HangaBatchFixture.NewOptions();
            options.ChromiumExecutablePath = Path.Combine(Path.GetTempPath(), "no-such-chromium");
            var ex = await Assert.ThrowsAsync<HangaBrowserException>(() => HangaBatch.StartAsync(options, HangaBatchFixture.ConfigureTestReports));
            Assert.Equal(HangaStage.BrowserLaunch, ex.Stage);

            await Assert.ThrowsAsync<ArgumentNullException>(() => HangaBatch.StartAsync(null!));
        }

        [Fact]
        public async Task 帳票1件あたりの時間()
        {
            // 要件5.3: 数値目標は持たない(ベストエフォート)。時間は出力に記録し、design.md「処理時間の目安」に転記する
            var startup = Stopwatch.StartNew();
            await using var batch = await HangaBatch.StartAsync(HangaBatchFixture.NewOptions(), HangaBatchFixture.ConfigureTestReports);
            startup.Stop();

            var first = Stopwatch.StartNew();
            await new Cshtml2Pdf(batch, "Invoice", "Invoice", HangaBatchFixture.Invoice("初回")).ToBytesAsync(); // ビューの初回の読み込みを含む
            first.Stop();

            const int count = 8;
            var sequential = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
            {
                await new Cshtml2Pdf(batch, "Invoice", "Invoice", HangaBatchFixture.Invoice("順" + i)).ToBytesAsync();
            }

            sequential.Stop();

            var parallel = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, count).Select(i =>
                new Cshtml2Pdf(batch, "Invoice", "Invoice", HangaBatchFixture.Invoice("並" + i)).ToBytesAsync()));
            parallel.Stop();

            output.WriteLine($"起動(ホストと Chromium): {startup.ElapsedMilliseconds} ms");
            output.WriteLine($"初回の帳票: {first.ElapsedMilliseconds} ms");
            output.WriteLine($"1件ずつ {count} 件: {sequential.ElapsedMilliseconds} ms(1件あたり {sequential.ElapsedMilliseconds / count} ms)");
            output.WriteLine($"並行 {count} 件(同時処理数 {HangaBatchFixture.NewOptions().MaxConcurrentRenders}): {parallel.ElapsedMilliseconds} ms(1件あたり {parallel.ElapsedMilliseconds / count} ms)");
            Assert.InRange(sequential.ElapsedMilliseconds / count, 0, 30_000);
        }

        private static void CopyDirectory(string source, string destination)
        {
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
    }
}
