using System;
using Hanga.Hosting;
using Hanga.Rendering;
using Hanga.Templating;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hanga
{
    /// <summary>Hanga を呼び出し元アプリに登録する(要件12.5, 12.6、design.md「公開API」)。</summary>
    public static class HangaServiceCollectionExtensions
    {
        /// <summary>コードで設定して登録する。</summary>
        public static IServiceCollection AddHanga(this IServiceCollection services, Action<HangaOptions> configure)
        {
            var options = new HangaOptions();
            configure(options);
            return Register(services, options);
        }

        /// <summary>設定ファイル(<c>appsettings.json</c> の節。例: <c>Configuration.GetSection("Hanga")</c>)から読んで登録する。</summary>
        public static IServiceCollection AddHanga(this IServiceCollection services, IConfiguration configuration)
        {
            return AddHanga(services, configuration, _ => { });
        }

        /// <summary>設定ファイルから読んだ後、コードで上書きして登録する。</summary>
        public static IServiceCollection AddHanga(this IServiceCollection services, IConfiguration configuration, Action<HangaOptions> configure)
        {
            var options = new HangaOptions();
            configuration.Bind(options);
            configure(options);
            return Register(services, options);
        }

        private static IServiceCollection Register(IServiceCollection services, HangaOptions options)
        {
            // 登録時に検証し、誤りがあればアプリの起動前に知らせる(要件12.6、design.md「登録」)
            options.Validate();

            services.TryAddSingleton(options);
            services.TryAddSingleton<PipelineHolder>();
            services.AddTransient<IStartupFilter, PipelineCaptureStartupFilter>();
            services.TryAddSingleton(sp => new GlyphSupport(options));
            services.TryAddSingleton(sp => new BrowserHost(
                options,
                sp.GetService<ILogger<BrowserHost>>(),
                sp.GetRequiredService<GlyphSupport>().VerifyGaijiFontAsync));
            services.TryAddSingleton(sp => new ReportRenderer(
                sp.GetRequiredService<BrowserHost>(),
                options,
                sp.GetRequiredService<GlyphSupport>(),
                sp.GetService<ILogger<ReportRenderer>>()));
            services.TryAddSingleton<ViewHtmlRenderer>();
            services.TryAddSingleton(sp => new HangaPdfConverter(
                options,
                sp.GetRequiredService<ViewHtmlRenderer>(),
                sp.GetRequiredService<ReportRenderer>(),
                sp.GetRequiredService<BrowserHost>(),
                sp.GetRequiredService<PipelineHolder>(),
                sp.GetService<ILogger<HangaPdfConverter>>()));
            services.AddHostedService<HangaHostedService>();
            return services;
        }
    }
}
