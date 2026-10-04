using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hanga.Hosting;
using Hanga.Rendering;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hanga.Tests
{
    /// <summary>仮想オリジンへの要求をアプリのパイプラインにプロセス内で渡す(要件3.1〜3.3)。Chromium は使わない。</summary>
    public class PipelineForwarderTests : IClassFixture<HangaAppFactory>
    {
        private readonly HangaAppFactory factory;

        public PipelineForwarderTests(HangaAppFactory factory)
        {
            this.factory = factory;
            _ = factory.Server; // アプリを起動してパイプラインを組み立てさせる
        }

        [Fact]
        public async Task 静的ファイルをアプリのUseStaticFilesで返す()
        {
            var response = await Forward(Get("/css/site.css", "?v=abc"));
            Assert.Equal(200, response.StatusCode);
            Assert.StartsWith("text/css", response.ContentType);
            Assert.Contains("font-family", Encoding.UTF8.GetString(response.Body));
        }

        [Fact]
        public async Task ログインが必要なAPIはCookieが無ければ失敗の応答()
        {
            // Cookie 認証は要求の種類によって 302(ログイン画面への転送)か 401 を返す。どちらも Hanga は失敗として扱う(要件3.5)
            var response = await Forward(Get("/api/orders/5"));
            Assert.InRange(response.StatusCode, 300, 499);
        }

        [Fact]
        public async Task 元の要求のCookieを引き継いでAPIがオペレーターの権限で応答する()
        {
            string cookie = await SignInAsync("operator1");
            var response = await Forward(Get("/api/orders/5"), cookie);
            Assert.Equal(200, response.StatusCode);
            string json = Encoding.UTF8.GetString(response.Body);
            Assert.Contains("ORD-5", json);
            Assert.Contains("operator1", json);
        }

        [Fact]
        public async Task Chromiumが付けたCookieは使わない()
        {
            var request = new VirtualRequest("GET", "/api/orders/5", string.Empty,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Cookie"] = await SignInAsync("intruder") }, null);
            var response = await Forward(request, cookie: null);
            Assert.InRange(response.StatusCode, 300, 499);
        }

        [Fact]
        public async Task 応答が上限を超えれば失敗の応答にする()
        {
            var snapshot = Snapshot(null);
            var forwarder = new PipelineForwarder(snapshot, Pipeline(), maxResponseBodyBytes: 10, NullLogger.Instance);
            var response = await forwarder.HandleAsync(Get("/css/site.css"), CancellationToken.None);
            Assert.Equal(502, response.StatusCode);
        }

        private Task<VirtualResponse> Forward(VirtualRequest request, string? cookie = null) =>
            new PipelineForwarder(Snapshot(cookie), Pipeline(), 50L * 1024 * 1024, NullLogger.Instance).HandleAsync(request, CancellationToken.None);

        private RequestDelegate Pipeline() => factory.Services.GetRequiredService<PipelineHolder>().GetRequired();

        private RequestSnapshot Snapshot(string? cookie)
        {
            var context = new DefaultHttpContext { RequestServices = factory.Services };
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("localhost");
            if (cookie != null)
            {
                context.Request.Headers["Cookie"] = cookie;
            }

            return RequestSnapshot.From(context);
        }

        private static VirtualRequest Get(string path, string query = "") =>
            new VirtualRequest("GET", path, query, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), null);

        /// <summary>テスト用のログインを行い、認証 Cookie(Cookie ヘッダーの値)を返す。</summary>
        internal static async Task<string> SignInAsync(HangaAppFactory factory, string user)
        {
            using var client = factory.NewClient();
            HttpResponseMessage response = await client.GetAsync("/Account/SignIn?user=" + Uri.EscapeDataString(user));
            response.EnsureSuccessStatusCode();
            return string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        }

        private Task<string> SignInAsync(string user) => SignInAsync(factory, user);
    }
}
