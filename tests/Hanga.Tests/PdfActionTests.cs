using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Hanga.TestSupport;
using Xunit;

namespace Hanga.Tests
{
    /// <summary>PDF 用アクションからの生成(要件1, 3, 7〜9)。テスト用アプリと Chromium を使う。</summary>
    public class PdfActionTests : IClassFixture<HangaAppFactory>
    {
        private readonly HangaAppFactory factory;

        public PdfActionTests(HangaAppFactory factory)
        {
            this.factory = factory;
        }

        [Fact]
        public async Task ブラウザで開く形でPDFを返す()
        {
            using var client = factory.NewClient();
            HttpResponseMessage response = await client.GetAsync("/Pdf/Index");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("inline", response.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal("ホーム画面.pdf", response.Content.Headers.ContentDisposition.FileNameStar);

            var pdf = PdfInspector.Read(await response.Content.ReadAsByteArrayAsync());
            Assert.Contains("モデル: 注文123", pdf.AllText);       // モデル
            Assert.Contains("site.js が実行された", pdf.AllText);  // レイアウトが読み込む JavaScript
            Assert.Equal("Home page - TestApp", pdf.Title);         // レイアウトの <title>(ViewData)
        }

        [Fact]
        public async Task ダウンロードの形でコントローラーのViewBagを引き継ぐ()
        {
            using var client = factory.NewClient();
            HttpResponseMessage response = await client.GetAsync("/Pdf/Report");

            Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal("帳票 2026年10月.pdf", response.Content.Headers.ContentDisposition.FileNameStar);
            var pdf = PdfInspector.Read(await response.Content.ReadAsByteArrayAsync());
            Assert.Contains("ViewBag: コントローラーの ViewBag", pdf.AllText);
            Assert.Contains("モデル: 帳票のモデル", pdf.AllText);
        }

        [Fact]
        public async Task オプションで横向きにできる()
        {
            using var client = factory.NewClient();
            var pdf = PdfInspector.Read(await client.GetByteArrayAsync("/Pdf/Index?orientation=landscape"));
            Assert.True(pdf.PageSizes[0].Width > pdf.PageSizes[0].Height);
        }

        [Fact]
        public async Task ログインしたオペレーターの権限でAPIから取得した値がPDFに入る()
        {
            // 要件3.2, 3.8
            using var client = await SignedInClientAsync("user1");
            var pdf = PdfInspector.Read(await client.GetByteArrayAsync("/Pdf/Order"));
            Assert.Contains("ORD-123", pdf.AllText);
            Assert.Contains("user1 が取得", pdf.AllText);
            Assert.DoesNotContain("読み込み中", pdf.AllText);
        }

        [Fact]
        public async Task ログインが切れていればエラーにしCookieの値を含めない()
        {
            // 要件3.5, 8.4, 8.5: API がログイン画面へ転送(または 401)される場合は、値が欠けた PDF を返さない
            using var client = factory.NewClient();
            client.DefaultRequestHeaders.Add("Cookie", "unrelated=secret-cookie-value");
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("/Pdf/OrderWithoutLogin"));

            var hanga = Assert.IsType<HangaResourceRequestException>(Unwrap(ex));
            Assert.Contains(hanga.FailedRequests, f => f.Url.EndsWith("/api/orders/123", StringComparison.Ordinal) && f.StatusCode >= 300);
            Assert.DoesNotContain("secret-cookie-value", hanga.Message);
        }

        [Fact]
        public async Task 同時に要求しても各PDFには本人の値だけが入る()
        {
            // 要件9.1〜9.4(検証コード concurrent-test.sh の内容)
            var users = Enumerable.Range(1, 8).Select(i => "user" + i).ToList();
            var clients = await Task.WhenAll(users.Select(SignedInClientAsync));
            try
            {
                var pdfs = await Task.WhenAll(clients.Select(c => c.GetByteArrayAsync("/Pdf/Order")));
                for (int i = 0; i < users.Count; i++)
                {
                    string text = PdfInspector.Read(pdfs[i]).AllText;
                    Assert.Contains(users[i] + " が取得", text);
                    Assert.DoesNotContain(users.Where(u => u != users[i]).Select(u => u + " が取得"), text.Contains);
                }
            }
            finally
            {
                foreach (var client in clients)
                {
                    client.Dispose();
                }
            }
        }

        [Fact]
        public async Task 既定では外部への要求の遮断を警告にする()
        {
            using var client = factory.NewClient();
            var warnings = await WarningsAsync(client, "/Pdf/Warnings?view=External");
            Assert.Contains(warnings, w => w.Contains("BlockedExternalRequest") && w.Contains("https://cdn.example.com/lib.js"));
        }

        [Fact]
        public async Task 帳票ごとに厳格な扱いにできる()
        {
            // 要件8.7: 帳票ごとの設定でアプリ全体の設定を上書きする
            using var client = factory.NewClient();
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("/Pdf/Warnings?view=External&strict=true"));
            Assert.IsType<HangaStrictModeException>(Unwrap(ex));
        }

        private async Task<HttpClient> SignedInClientAsync(string user)
        {
            var client = factory.NewClient();
            client.DefaultRequestHeaders.Add("Cookie", await PipelineForwarderTests.SignInAsync(factory, user));
            return client;
        }

        internal static async Task<List<string>> WarningsAsync(HttpClient client, string url)
        {
            string text = await client.GetStringAsync(url);
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        /// <summary>テスト用のホストは、アプリの例外を呼び出し側に投げ直す。内側の Hanga の例外を取り出す。</summary>
        internal static Exception Unwrap(Exception ex)
        {
            Exception current = ex;
            while (!(current is HangaException) && current.InnerException != null)
            {
                current = current.InnerException;
            }

            return current;
        }
    }

    /// <summary>アプリ全体の設定で厳格な扱いにした場合(要件8.7)。</summary>
    public class StrictAppTests : IDisposable
    {
        private readonly HangaAppFactory factory = new HangaAppFactory(new Dictionary<string, string> { ["Hanga:Strict"] = "true" });

        [Fact]
        public async Task アプリ全体で厳格にすると警告の対象をエラーにする()
        {
            using var client = factory.NewClient();
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("/Pdf/Warnings?view=External"));
            Assert.IsType<HangaStrictModeException>(PdfActionTests.Unwrap(ex));
        }

        [Fact]
        public async Task 帳票ごとの設定で厳格な扱いを外せる()
        {
            using var client = factory.NewClient();
            var warnings = await PdfActionTests.WarningsAsync(client, "/Pdf/Warnings?view=External&strict=false");
            Assert.Contains(warnings, w => w.Contains("BlockedExternalRequest"));
        }

        public void Dispose() => factory.Dispose();
    }
}
