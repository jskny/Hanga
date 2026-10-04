using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Hanga.Hosting
{
    /// <summary>
    /// バッチの中に、Web サーバーを持たない ASP.NET Core のホストを組み立てる(batch-pdf-generation の design.md「ホストの組み立て」、要件1.1, 2.1, 2.2, 3.1〜3.3)。
    /// ビューの描画(MVC)・静的ファイル・ルーティングを用意し、仮想オリジンへの要求はこのホストのパイプラインに渡す。
    /// </summary>
    internal static class BatchHostBuilder
    {
        public static IHost Build(HangaOptions options, HangaBatchOptions batchOptions)
        {
            string contentRoot = batchOptions.ResolvedContentRootPath;
            return new HostBuilder()
                .UseContentRoot(contentRoot)
                .ConfigureWebHost(web =>
                {
                    if (!string.IsNullOrWhiteSpace(batchOptions.WebRootPath))
                    {
                        web.UseWebRoot(batchOptions.ResolvePath(batchOptions.WebRootPath!));
                    }

                    // 開発中の実行(ビルドの出力)では、帳票ライブラリの wwwroot を /_content/<ライブラリ名>/ に重ねる。
                    // 発行したバッチには一覧(<バッチ名>.StaticWebAssets.xml)が無く、何もしない(発行先の wwwroot/_content/ から応答する)
                    web.UseStaticWebAssets();

                    // ポートを開かない(要件1.1)。ホストの起動でルーティングなどの初期化だけが行われる
                    web.UseServer(new NoopServer());
                    web.ConfigureServices(services =>
                    {
                        IMvcBuilder mvc = services.AddControllersWithViews();
                        if (batchOptions.ViewAssemblies.Count > 0)
                        {
                            mvc.ConfigureApplicationPartManager(manager => AddViewAssemblies(manager, batchOptions.ViewAssemblies));
                        }

                        HangaServiceCollectionExtensions.Register(services, options);
                        batchOptions.ConfigureServices?.Invoke(services);
                    });
                    web.Configure(app =>
                    {
                        // パイプライン全体を捕まえるミドルウェアは、Hanga の IStartupFilter が先頭に加える(中核機能と同じ)
                        app.UseStaticFiles();
                        foreach (var mapping in batchOptions.StaticFileMappings)
                        {
                            app.UseStaticFiles(new StaticFileOptions
                            {
                                RequestPath = new PathString(mapping.Key.TrimEnd('/')),
                                FileProvider = new PhysicalFileProvider(batchOptions.ResolvePath(mapping.Value)),
                            });
                        }

                        // タグヘルパーの URL の生成(asp-controller/asp-action)に必要(検証レポート「6.4」)
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapControllers());
                    });
                })
                .Build();
        }

        /// <summary>
        /// 指定のアセンブリと、関連するビューのアセンブリ(.NET 5 の Razor SDK では <c>&lt;名前&gt;.Views.dll</c>)をアプリケーションパーツに加える(要件2.2)。
        /// 自動で見つかった部品と重ねないよう、同じ種類・同じ名前の部品が既にあれば加えない。
        /// </summary>
        private static void AddViewAssemblies(ApplicationPartManager manager, IEnumerable<Assembly> assemblies)
        {
            foreach (Assembly assembly in assemblies.Distinct())
            {
                foreach (Assembly target in new[] { assembly }.Concat(RelatedAssemblyAttribute.GetRelatedAssemblies(assembly, throwOnError: true)))
                {
                    foreach (ApplicationPart part in ApplicationPartFactory.GetApplicationPartFactory(target).GetApplicationParts(target))
                    {
                        if (!manager.ApplicationParts.Any(p => p.GetType() == part.GetType() && p.Name == part.Name))
                        {
                            manager.ApplicationParts.Add(part);
                        }
                    }
                }
            }
        }
    }

    /// <summary>ポートを開かない <see cref="IServer"/>(検証レポート「6.4」)。要求は受け付けず、Hanga がパイプラインを直接呼ぶ。</summary>
    internal sealed class NoopServer : IServer
    {
        public IFeatureCollection Features { get; } = new FeatureCollection();

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
