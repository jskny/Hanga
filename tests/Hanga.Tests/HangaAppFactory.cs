using System.Collections.Generic;
using System.Net.Http;
using Hanga.TestApp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Hanga.Tests
{
    /// <summary>テスト用アプリを、設定("Hanga" 節など)を変えて起動する。</summary>
    public sealed class HangaAppFactory : WebApplicationFactory<Startup>
    {
        private readonly IDictionary<string, string> settings;

        public HangaAppFactory()
            : this(new Dictionary<string, string>())
        {
        }

        internal HangaAppFactory(IDictionary<string, string> settings)
        {
            this.settings = settings;
        }

        /// <summary>
        /// テスト用のクライアントを作る。Cookie は自動で扱わず、テストが Cookie ヘッダーを明示する。
        /// Microsoft.AspNetCore.Mvc.Testing 5.0.17 の Cookie の自動処理は、新しいランタイム(RollForward 先の .NET 10)で
        /// 空の Cookie ヘッダーを加えようとして FormatException になるため(docs/開発環境メモ.md)。
        /// </summary>
        public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            foreach (var setting in settings)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }
        }
    }
}
