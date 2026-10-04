using System;
using System.Net;
using System.Threading.Tasks;
using Hanga.TestApp;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hanga.Templating.Tests
{
    /// <summary>
    /// テスト用アプリ(Hanga.TestApp)のビューを、アプリ自身のビュー描画の仕組みで HTML にする(要件1.1〜1.5)。
    /// テストでは元の要求が無いため、要求の情報をテスト側で用意する(要件1.6 の「引数として受け取る形」の確認を兼ねる)。
    /// </summary>
    public class ViewHtmlRendererTests : IClassFixture<WebApplicationFactory<Startup>>
    {
        private readonly WebApplicationFactory<Startup> factory;

        public ViewHtmlRendererTests(WebApplicationFactory<Startup> factory)
        {
            this.factory = factory;
            _ = factory.Server; // ホストを起動し、ルーティングの登録を済ませる(タグヘルパーによる URL の生成に必要)
        }

        [Fact]
        public async Task レイアウト_ViewData_パス_タグヘルパーが画面と同じになる()
        {
            string html = await RenderAsync("Home", "Index", model: "注文123");

            Assert.Contains("<title>Home page - TestApp</title>", html);                   // _ViewStart のレイアウトと ViewData
            Assert.Matches("href=\"/css/site.css\\?v=[^\"]+\"", html);                     // ~/ の解決と asp-append-version
            Assert.Matches("src=\"/js/site.js\\?v=[^\"]+\"", html);
            Assert.Contains("<a href=\"/Home/Privacy\">Privacy</a>", html);              // 部分ビューと asp-controller/asp-action
            Assert.Contains("モデル: 注文123", html);
        }

        [Fact]
        public async Task セクションがレイアウトに出力される()
        {
            string html = await RenderAsync("Home", "Order");
            Assert.Matches("src=\"/js/order.js\\?v=[^\"]+\"", html);
        }

        [Fact]
        public async Task 引き継いだViewDataを使い元は書き換えない()
        {
            using var scope = factory.Services.CreateScope();
            var source = new ViewDataDictionary(scope.ServiceProvider.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary());
            source["Message"] = "ViewBag の値";
            var request = new ViewRenderRequest(NewHttpContext(scope), "Home", "Index", model: null) { ViewData = source };

            string html = Decode(await new ViewHtmlRenderer().RenderAsync(request));

            Assert.Contains("ViewBag: ViewBag の値", html);
            Assert.False(source.ContainsKey("Title")); // ビューが設定した Title が元の ViewData に漏れない
        }

        [Fact]
        public async Task 指定したコントローラー名のフォルダからビューを探す()
        {
            // 要件1.3: 元の要求とは別のコントローラーのビューも指定できる
            string html = await RenderAsync("Reports", "Simple");
            Assert.Contains("Reports フォルダの帳票", html);
        }

        [Fact]
        public async Task パスでビューを指定できる()
        {
            string html = await RenderAsync("Home", "~/Views/Reports/Simple.cshtml");
            Assert.Contains("Reports フォルダの帳票", html);
        }

        [Fact]
        public async Task ビューが見つからなければ探した場所を含むエラー()
        {
            var ex = await Assert.ThrowsAsync<HangaViewNotFoundException>(() => RenderAsync("Home", "Missing"));
            Assert.Contains("/Views/Home/Missing.cshtml", ex.SearchedLocations);
            Assert.Contains("/Views/Shared/Missing.cshtml", ex.SearchedLocations);
            Assert.Equal(HangaStage.ViewRendering, ex.Stage);
        }

        [Fact]
        public async Task 描画中の例外は元の例外を含むエラーにする()
        {
            var ex = await Assert.ThrowsAsync<HangaViewRenderingException>(() => RenderAsync("Home", "Broken"));
            Assert.IsType<InvalidOperationException>(ex.InnerException);
            Assert.Contains("壊れたビュー", ex.Message);
        }

        private async Task<string> RenderAsync(string controller, string view, object? model = null)
        {
            using var scope = factory.Services.CreateScope();
            return Decode(await new ViewHtmlRenderer().RenderAsync(new ViewRenderRequest(NewHttpContext(scope), controller, view, model)));
        }

        /// <summary>
        /// ASP.NET Core は既定で、ビューに出力する日本語などを文字参照(&amp;#x6CE8; 等)にする。ブラウザでは元の文字として表示されるため、
        /// テストでは文字参照を元に戻してから比べる。
        /// </summary>
        private static string Decode(string html) => WebUtility.HtmlDecode(html);

        private static HttpContext NewHttpContext(IServiceScope scope)
        {
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("localhost");
            // エンドポイントルーティングで URL を生成させるため、ダミーのエンドポイントを設定する(検証レポート「6.4」)
            http.SetEndpoint(new Endpoint(null, EndpointMetadataCollection.Empty, "test"));
            return http;
        }
    }
}
