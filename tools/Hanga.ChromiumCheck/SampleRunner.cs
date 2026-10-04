using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Hanga.Sample.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hanga.ChromiumCheck
{
    /// <summary>サンプルアプリをローカルホストで起動し、各帳票の PDF を集める。</summary>
    internal sealed class SampleRunner
    {
        private readonly CheckOptions options;

        public SampleRunner(CheckOptions options)
        {
            this.options = options;
        }

        /// <summary>集めた結果を <paramref name="outputDir"/> に保存し、Chromium の版を返す。</summary>
        public async Task<string> CollectAsync(string outputDir)
        {
            Directory.CreateDirectory(outputDir);
            using IHost host = BuildHost();
            await host.StartAsync().ConfigureAwait(false);
            try
            {
                string baseUrl = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
                var cookies = new CookieContainer();
                using var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(2) };
                (await client.GetAsync("/Account/SignIn?user=chromium-check").ConfigureAwait(false)).EnsureSuccessStatusCode();

                foreach (string name in ReportsController.Names)
                {
                    HttpResponseMessage response = await client.GetAsync("/Reports/" + name + "Pdf").ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException($"帳票 {name} の PDF を作れませんでした(HTTP {(int)response.StatusCode})。");
                    }

                    await File.WriteAllBytesAsync(Path.Combine(outputDir, name + ".pdf"), await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).ConfigureAwait(false);
                }

                string version = host.Services.GetRequiredService<HangaPdfConverter>().ChromiumVersion ?? "不明";
                await File.WriteAllTextAsync(Path.Combine(outputDir, "chromium-version.txt"), version + Environment.NewLine).ConfigureAwait(false);
                return version;
            }
            finally
            {
                await host.StopAsync().ConfigureAwait(false);
            }
        }

        private IHost BuildHost()
        {
            var settings = new Dictionary<string, string>
            {
                ["Hanga:ChromiumExecutablePath"] = options.Chromium,
            };
            for (int i = 0; i < options.ChromiumArguments.Count; i++)
            {
                settings["Hanga:ChromiumArguments:" + i] = options.ChromiumArguments[i];
            }

            if (!string.IsNullOrEmpty(options.GaijiFont))
            {
                settings["Hanga:GaijiFontFamily"] = options.GaijiFont!;
            }

            return Host.CreateDefaultBuilder()
                .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
                // サンプルアプリの appsettings.json より優先させるため、最後に加える
                .ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings))
                .ConfigureWebHostDefaults(web =>
                {
                    // サンプルアプリのコントローラー・ビューを使うため、アプリ名をサンプルアプリにする(既定はこのツール)
                    web.UseSetting(WebHostDefaults.ApplicationKey, typeof(Hanga.Sample.Startup).Assembly.GetName().Name);
                    web.UseContentRoot(AppContext.BaseDirectory);
                    web.UseUrls("http://127.0.0.1:0"); // ローカルホストのみ、空いているポート
                    web.UseStartup<Hanga.Sample.Startup>();
                })
                .Build();
        }

    }
}
