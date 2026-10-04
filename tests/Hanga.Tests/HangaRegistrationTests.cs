using System;
using System.Collections.Generic;
using System.Linq;
using Hanga.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hanga.Tests
{
    /// <summary>登録(AddHanga)と設定ファイルからの読み込み(要件12.5, 12.6)。</summary>
    public class HangaRegistrationTests
    {
        private static IConfiguration Config(params (string Key, string Value)[] values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

        [Fact]
        public void 設定ファイルの値を読む()
        {
            var services = new ServiceCollection();
            services.AddHanga(Config(
                ("ChromiumExecutablePath", "/usr/bin/chrome"),
                ("ChromiumArguments:0", "--no-sandbox"),
                ("MaxConcurrentRenders", "8"),
                ("RenderTimeout", "00:00:12"),
                ("AllowedExternalHosts:0", "cdn.example.com"),
                ("GaijiFontFamily", "IPAmj明朝"),
                ("Strict", "true")));

            var options = services.BuildServiceProvider().GetRequiredService<HangaOptions>();
            Assert.Equal("/usr/bin/chrome", options.ChromiumExecutablePath);
            Assert.Equal(new[] { "--no-sandbox" }, options.ChromiumArguments);
            Assert.Equal(8, options.MaxConcurrentRenders);
            Assert.Equal(TimeSpan.FromSeconds(12), options.RenderTimeout);
            Assert.Equal(new[] { "cdn.example.com" }, options.AllowedExternalHosts);
            Assert.Equal("IPAmj明朝", options.GaijiFontFamily);
            Assert.True(options.Strict);
        }

        [Fact]
        public void 設定ファイルに書かなかった項目は既定値()
        {
            var services = new ServiceCollection();
            services.AddHanga(Config(("ChromiumExecutablePath", "/usr/bin/chrome")));
            var options = services.BuildServiceProvider().GetRequiredService<HangaOptions>();
            Assert.Equal(4, options.MaxConcurrentRenders);
            Assert.Equal(TimeSpan.FromSeconds(30), options.RenderTimeout);
        }

        [Fact]
        public void 設定ファイルの値をコードで上書きできる()
        {
            var services = new ServiceCollection();
            services.AddHanga(Config(("ChromiumExecutablePath", "/usr/bin/chrome"), ("MaxConcurrentRenders", "8")), o => o.MaxConcurrentRenders = 2);
            Assert.Equal(2, services.BuildServiceProvider().GetRequiredService<HangaOptions>().MaxConcurrentRenders);
        }

        [Fact]
        public void 不正な値は登録時にエラー()
        {
            var services = new ServiceCollection();
            var ex = Assert.Throws<HangaConfigurationException>(() =>
                services.AddHanga(Config(("ChromiumExecutablePath", "/usr/bin/chrome"), ("MaxConcurrentRenders", "0"))));
            Assert.Contains("MaxConcurrentRenders", ex.Message);
        }

        [Fact]
        public void 変換器とパイプラインの捕捉が登録される()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHanga(o => o.ChromiumExecutablePath = "/usr/bin/chrome");
            var provider = services.BuildServiceProvider();
            Assert.NotNull(provider.GetRequiredService<HangaPdfConverter>());
            Assert.Contains(provider.GetServices<IStartupFilter>(), f => f is PipelineCaptureStartupFilter);
            Assert.Same(provider.GetRequiredService<HangaPdfConverter>(), provider.GetRequiredService<HangaPdfConverter>()); // 共有(シングルトン)
        }

        [Fact]
        public void 登録していなければCshtml2Pdfの作成時にエラー()
        {
            var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
            var ex = Assert.Throws<HangaConfigurationException>(() => new Cshtml2Pdf(context, "Home", "Index"));
            Assert.Contains("AddHanga", ex.Message);
        }
    }
}
