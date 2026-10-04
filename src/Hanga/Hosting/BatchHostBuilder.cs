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
                    // Web のホストは ASPNETCORE_ で始まる環境変数を読む。バッチの動作が環境変数で変わらないよう、
                    // コンテンツのルート・Web ルート・環境名を固定し、外部のアセンブリによる起動時の処理(Hosting Startup)を読み込ませない
                    web.UseContentRoot(contentRoot);
                    web.UseWebRoot(batchOptions.ResolvePath(string.IsNullOrWhiteSpace(batchOptions.WebRootPath) ? "wwwroot" : batchOptions.WebRootPath!));
                    web.UseEnvironment(Environments.Production);
                    web.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");

                    // 開発中の実行(ビルドの出力)では、帳票ライブラリの wwwroot を /_content/<ライブラリ名>/ に重ねる。
                    // 発行したバッチには一覧(<バッチ名>.StaticWebAssets.xml)が無く、何もしない(発行先の wwwroot/_content/ から応答する)
                    web.UseStaticWebAssets();

                    // ポートを開かない(要件1.1)。ホストの起動でルーティングなどの初期化だけが行われる
                    web.UseServer(new NoopServer());
                    web.ConfigureServices(services =>
                    {
                        // 汎用ホストの既定(ConsoleLifetime)は Ctrl+C とプロセスの終了を横取りし、バッチの終了を妨げるため、何もしないものに替える
                        services.AddSingleton<IHostLifetime, BatchHostLifetime>();
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

                        // ルーティングは、タグヘルパーの URL の生成(asp-controller/asp-action)のためだけに使う(検証レポート「6.4」)。
                        // バッチには認証・認可が無いため、コントローラーのアクションは実行させない(帳票のページからの要求は 404 にする。セキュリティレビューの指摘)
                        app.UseRouting();
                        app.Run(context =>
                        {
                            context.Response.StatusCode = StatusCodes.Status404NotFound;
                            return Task.CompletedTask;
                        });
                        app.UseEndpoints(endpoints =>
                        {
                            // 属性で経路を指定したコントローラーと、既定の経路({controller=Home}/{action=Index}/{id?})のコントローラーの URL を生成できるようにする
                            endpoints.MapControllers();
                            endpoints.MapDefaultControllerRoute();
                        });
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
                foreach (Assembly target in new[] { assembly }.Concat(RelatedAssemblyAttribute.GetRelatedAssemblies(assembly, throwOnError: false)))
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

    /// <summary>
    /// バッチのホストの寿命の管理(何もしない)。既定の <c>ConsoleLifetime</c> は Ctrl+C を握りつぶし、プロセスの終了時にホストの破棄を待つため、
    /// バッチの Ctrl+C・<c>Environment.Exit</c> を妨げる。ホストの停止は <see cref="HangaBatch"/> の終了で行う。
    /// </summary>
    internal sealed class BatchHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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
