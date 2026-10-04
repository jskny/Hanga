using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hanga.Rendering;
using Hanga.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hanga.Tests
{
    /// <summary>
    /// バッチ(Web アプリとは別の実行ファイル)での生成(batch-pdf-generation の要件1〜4)。
    /// テスト用の帳票ライブラリ(Razor クラスライブラリ)のビューを、Web サーバーを起動せずに PDF にする。Chromium を使う。
    /// </summary>
    public class BatchTests : IClassFixture<HangaBatchFixture>
    {
        private readonly HangaBatch batch;

        public BatchTests(HangaBatchFixture fixture)
        {
            batch = fixture.Batch;
        }

        [Fact]
        public async Task 帳票ライブラリのビューからモデルとCSSとJavaScriptを反映したPDFを作る()
        {
            // 要件2.1, 3.1, 3.2: レイアウト・_ViewStart・部分ビュー・~/_content/... の CSS と JavaScript
            var pdf = new Cshtml2Pdf(batch, "Invoice", "Invoice", HangaBatchFixture.Invoice("山田太郎"));
            HangaPdfDocument document = await pdf.GenerateAsync();

            var inspector = PdfInspector.Read(document.Content);
            Assert.Contains("宛名: 山田太郎 様", inspector.AllText);
            Assert.Contains("消しゴム", inspector.AllText);                // 部分ビュー
            Assert.Contains("合計: 1,500 円", inspector.AllText);
            Assert.Contains("(CSS適用)", inspector.AllText);             // 帳票ライブラリの CSS
            Assert.Contains("JS実行済み", inspector.AllText);             // 帳票ライブラリの JavaScript
            Assert.Equal("請求書 山田太郎", inspector.Title);             // レイアウトの <title>(ViewData)
            Assert.True(document.Warnings.Count == 0, string.Join(" / ", document.Warnings.Select(w => w.Kind + ": " + w.Message + " " + w.Detail)));
            Assert.False(string.IsNullOrEmpty(batch.ChromiumVersion));
        }

        [Fact]
        public async Task パスで指定したビューと注入したサービスを使える()
        {
            // 要件2.3, 2.5
            var pdf = new Cshtml2Pdf(batch, "Other", "~/Views/Invoice/WithService.cshtml", HangaBatchFixture.Invoice("佐藤花子"));
            var inspector = PdfInspector.Read(await pdf.ToBytesAsync());
            Assert.Contains("発行元: テスト商事", inspector.AllText);
            Assert.Contains("宛名: 佐藤花子 様", inspector.AllText);
        }

        [Fact]
        public async Task 並行して生成した帳票にそれぞれのモデルの値だけが入る()
        {
            // 要件1.5, 2.4, 5.2
            string[] names = Enumerable.Range(1, 8).Select(i => "顧客" + i).ToArray();
            byte[][] pdfs = await Task.WhenAll(names.Select(name =>
                new Cshtml2Pdf(batch, "Invoice", "Invoice", HangaBatchFixture.Invoice(name)).ToBytesAsync()));

            for (int i = 0; i < names.Length; i++)
            {
                string text = PdfInspector.Read(pdfs[i]).AllText;
                Assert.Contains($"宛名: {names[i]} 様", text);
                Assert.All(names.Where((_, j) => j != i), other => Assert.DoesNotContain($"宛名: {other} 様", text));
            }
        }

        [Fact]
        public async Task 失敗した帳票は原因の分かる例外になり後の帳票は生成できる()
        {
            // 要件2.6, 3.5, 4.3, 4.4
            var notFound = await Assert.ThrowsAsync<HangaViewNotFoundException>(
                () => new Cshtml2Pdf(batch, "Invoice", "NoSuchView", HangaBatchFixture.Invoice("A")).ToBytesAsync());
            Assert.Contains(notFound.SearchedLocations, l => l.Contains("/Views/Invoice/NoSuchView.cshtml", StringComparison.Ordinal));

            var broken = await Assert.ThrowsAsync<HangaViewRenderingException>(
                () => new Cshtml2Pdf(batch, "Invoice", "Broken").ToBytesAsync());
            Assert.Equal(HangaStage.ViewRendering, broken.Stage);

            // バッチにはセッションが無い(design.md「帳票1件用の HttpContext」)
            await Assert.ThrowsAsync<HangaViewRenderingException>(() => new Cshtml2Pdf(batch, "Invoice", "UsesSession").ToBytesAsync());

            var missing = await Assert.ThrowsAsync<HangaResourceRequestException>(
                () => new Cshtml2Pdf(batch, "Invoice", "MissingAsset", HangaBatchFixture.Invoice("B")).ToBytesAsync());
            Assert.Contains(missing.FailedRequests, f => f.Url.EndsWith("/_content/Hanga.TestReports/css/missing.css", StringComparison.Ordinal) && f.StatusCode == 404);

            var after = PdfInspector.Read(await new Cshtml2Pdf(batch, "Invoice", "Invoice", HangaBatchFixture.Invoice("失敗の後")).ToBytesAsync());
            Assert.Contains("宛名: 失敗の後 様", after.AllText);
        }

        [Fact]
        public async Task 外部への読み込みは遮断して警告し厳格な扱いではエラーにする()
        {
            // 要件3.6(中核機能の要件3.4, 3.6, 8.7)
            HangaPdfDocument document = await new Cshtml2Pdf(batch, "Invoice", "External").GenerateAsync();
            Assert.Contains(document.Warnings, w => w.Kind == HangaWarningKind.BlockedExternalRequest);

            var strict = new Cshtml2Pdf(batch, "Invoice", "External");
            strict.Options.Strict = true;
            await Assert.ThrowsAsync<HangaStrictModeException>(() => strict.ToBytesAsync());
        }

        [Fact]
        public async Task ストリームに書き込みChromiumを使い回す()
        {
            // 要件4.1, 5.1
            int processId = batch.Services.GetRequiredService<BrowserHost>().CurrentBrowser!.Process!.Id;
            using var stream = new MemoryStream();
            await new Cshtml2Pdf(batch, "Invoice", "Invoice", HangaBatchFixture.Invoice("ストリーム")).WriteToAsync(stream);
            Assert.Contains("宛名: ストリーム 様", PdfInspector.Read(stream.ToArray()).AllText);
            Assert.Equal(processId, batch.Services.GetRequiredService<BrowserHost>().CurrentBrowser!.Process!.Id);
        }

        [Fact]
        public void 帳票1件用の要求に認証情報を付けない()
        {
            // 要件2.4, 3.4
            var request = batch.CreateRequest();
            try
            {
                var snapshot = Hanga.Hosting.RequestSnapshot.From(request.HttpContext);
                Assert.Empty(snapshot.Cookie);
                Assert.Empty(snapshot.AcceptLanguage);
                Assert.Equal("https", snapshot.Scheme);
                Assert.Equal("hanga.invalid", snapshot.Host.Value);
            }
            finally
            {
                request.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        [Fact]
        public async Task Chromiumが終了しても次の帳票は生成できる()
        {
            // 要件4.4(中核機能の要件9.5)
            await new Cshtml2Pdf(batch, "Invoice", "Invoice", HangaBatchFixture.Invoice("終了前")).ToBytesAsync();
            var host = batch.Services.GetRequiredService<BrowserHost>();
            host.CurrentBrowser!.Process!.Kill(entireProcessTree: true);
            host.CurrentBrowser.Process.WaitForExit(10_000);

            var after = PdfInspector.Read(await new Cshtml2Pdf(batch, "Invoice", "Invoice", HangaBatchFixture.Invoice("終了後")).ToBytesAsync());
            Assert.Contains("宛名: 終了後 様", after.AllText);
        }

        [Fact]
        public async Task 取り消すと処理を中止し後の帳票は生成できる()
        {
            // 要件4.5
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new Cshtml2Pdf(batch, "Invoice", "Invoice", HangaBatchFixture.Invoice("取り消し")).ToBytesAsync(cts.Token));

            // 表示の完了を待っている途中(ブラウザコンテキストを使っている間)に取り消す
            var waiting = new Cshtml2Pdf(batch, "Invoice", "Invoice", HangaBatchFixture.Invoice("待機中に取り消し"));
            waiting.Options.ReadyExpression = "false";
            using var during = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var stopwatch = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.ToBytesAsync(during.Token));
            Assert.InRange(stopwatch.ElapsedMilliseconds, 0, 20_000); // 待機の上限(30秒)を待たずに中止する

            var after = PdfInspector.Read(await new Cshtml2Pdf(batch, "Invoice", "Invoice", HangaBatchFixture.Invoice("取り消しの後")).ToBytesAsync());
            Assert.Contains("宛名: 取り消しの後 様", after.AllText);
        }

        [Fact]
        public async Task ファイルに保存し一時ファイルを残さない()
        {
            // 要件4.1, 4.2
            using var dir = new TemporaryDirectory();
            string path = Path.Combine(dir.Path, "請求書_山田.pdf");
            await new Cshtml2Pdf(batch, "Invoice", "Invoice", HangaBatchFixture.Invoice("山田")).SaveAsync(path);

            Assert.Contains("宛名: 山田 様", PdfInspector.Read(File.ReadAllBytes(path)).AllText);
            Assert.Equal(new[] { path }, Directory.GetFiles(dir.Path));
        }

        [Fact]
        public async Task 保存に失敗しても既存のファイルを壊さず一時ファイルを残さない()
        {
            // 要件4.2: 生成の失敗・取り消しでは、保存先を変えない
            using var dir = new TemporaryDirectory();
            string path = Path.Combine(dir.Path, "既存.pdf");
            File.WriteAllText(path, "前回の結果");

            await Assert.ThrowsAsync<HangaViewNotFoundException>(
                () => new Cshtml2Pdf(batch, "Invoice", "NoSuchView").SaveAsync(path));
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => new Cshtml2Pdf(batch, "Invoice", "Invoice", HangaBatchFixture.Invoice("取り消し")).SaveAsync(path, cts.Token));
            }

            string newPath = Path.Combine(dir.Path, "新規.pdf");
            await Assert.ThrowsAsync<HangaViewRenderingException>(() => new Cshtml2Pdf(batch, "Invoice", "Broken").SaveAsync(newPath));

            Assert.Equal("前回の結果", File.ReadAllText(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(dir.Path));
        }

        [Fact]
        public async Task 書き込みに失敗した場合は一時ファイルを残さない()
        {
            // 要件4.2: 保存先が既存のフォルダで、ファイルとして書けない(名前の変更が失敗する)
            using var dir = new TemporaryDirectory();
            string path = Path.Combine(dir.Path, "フォルダ.pdf");
            Directory.CreateDirectory(path);

            // Linux では IOException、Windows では UnauthorizedAccessException になる
            var ex = await Assert.ThrowsAnyAsync<Exception>(
                () => new Cshtml2Pdf(batch, "Invoice", "Invoice", HangaBatchFixture.Invoice("書き込み")).SaveAsync(path));
            Assert.True(ex is IOException || ex is UnauthorizedAccessException, ex.GetType().FullName);
            Assert.Empty(Directory.GetFiles(dir.Path));
        }

        [Fact]
        public void 引数の誤りを知らせる()
        {
            Assert.Throws<ArgumentNullException>(() => new Cshtml2Pdf((HangaBatch)null!, "Invoice", "Invoice"));
            Assert.Throws<ArgumentException>(() => new Cshtml2Pdf(batch, " ", "Invoice"));
            Assert.Throws<ArgumentException>(() => new Cshtml2Pdf(batch, "Invoice", string.Empty));
        }

        /// <summary>テストごとの一時フォルダ。</summary>
        internal sealed class TemporaryDirectory : IDisposable
        {
            public TemporaryDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hanga-batch-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
