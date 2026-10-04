using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// 検証: Webアプリとは別の実行ファイルから、Razor クラスライブラリ(Reports)のビューと静的ファイルを使えるか。
// 引数: [explicit] … ビューのアセンブリを明示的にアプリケーションパーツに加える
// 結果は docs/PDF出力方式検証レポート.md「6.5」
namespace Batch
{
    public sealed class NoopServer : IServer
    {
        public IFeatureCollection Features { get; } = new FeatureCollection();
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, System.Threading.CancellationToken cancellationToken) where TContext : notnull => Task.CompletedTask;
        public Task StopAsync(System.Threading.CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }

    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            bool explicitParts = args.Contains("explicit");
            RequestDelegate? pipeline = null;
            using var host = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseContentRoot(AppContext.BaseDirectory)
                    .UseStaticWebAssets()
                    .UseServer(new NoopServer())
                    .ConfigureServices(s =>
                    {
                        var mvc = s.AddControllersWithViews();
                        if (explicitParts)
                        {
                            var asm = typeof(Reports.InvoiceModel).Assembly;
                            mvc.ConfigureApplicationPartManager(m =>
                            {
                                foreach (var a in new[] { asm }.Concat(RelatedAssemblyAttribute.GetRelatedAssemblies(asm, throwOnError: false)))
                                {
                                    foreach (var part in ApplicationPartFactory.GetApplicationPartFactory(a).GetApplicationParts(a))
                                    {
                                        // 自動で見つかった部品と重ねない(同じ名前の部品が既にあれば加えない)
                                        if (!m.ApplicationParts.Any(p => p.GetType() == part.GetType() && p.Name == part.Name)) m.ApplicationParts.Add(part);
                                    }
                                }
                            });
                        }
                    })
                    .Configure(app =>
                    {
                        app.Use(next => { pipeline = next; return next; });
                        app.UseStaticFiles();
                        app.UseRouting();
                        app.UseEndpoints(e => e.MapDefaultControllerRoute());
                    }))
                .Build();
            await host.StartAsync();
            var env = host.Services.GetRequiredService<IWebHostEnvironment>();
            Console.WriteLine("webrootprovider: " + env.WebRootFileProvider.GetType().Name + " exists=" + env.WebRootFileProvider.GetFileInfo("_content/Reports/css/report.css").Exists);
            Console.WriteLine($"app={env.ApplicationName} env={env.EnvironmentName} webroot={env.WebRootPath}");
            var parts = host.Services.GetRequiredService<ApplicationPartManager>().ApplicationParts.Select(p => p.Name);
            Console.WriteLine("parts: " + string.Join(", ", parts));

            using var scope = host.Services.CreateScope();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            http.SetEndpoint(new Endpoint(null, EndpointMetadataCollection.Empty, "batch"));
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("hanga.invalid");
            var routeData = new RouteData();
            routeData.Values["controller"] = "Invoice";
            routeData.Values["action"] = "Invoice";
            var actionContext = new ActionContext(http, routeData, new ActionDescriptor());
            var engine = scope.ServiceProvider.GetRequiredService<IRazorViewEngine>();
            var found = engine.FindView(actionContext, "Invoice", isMainPage: true);
            int failures = 0;
            if (!found.Success)
            {
                Console.WriteLine("VIEW NOT FOUND: " + string.Join(", ", found.SearchedLocations));
                failures++;
            }
            else
            {
                using var writer = new StringWriter();
                var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = new Reports.InvoiceModel { CustomerName = "山田太郎", Amount = 12345 } };
                var tempData = new TempDataDictionary(http, scope.ServiceProvider.GetRequiredService<ITempDataProvider>());
                await found.View.RenderAsync(new ViewContext(actionContext, found.View, viewData, tempData, writer, new HtmlHelperOptions()));
                Console.WriteLine("----- html -----\n" + writer + "\n----------------");
                if (!writer.ToString().Contains("?v=")) { Console.WriteLine("asp-append-version NOT applied"); failures++; }
            }

            // 帳票のページが読み込む静的ファイルを、パイプラインにプロセス内で要求する(Hanga の PipelineForwarder と同じ方法)
            using var scope2 = host.Services.CreateScope();
            var req = new DefaultHttpContext { RequestServices = scope2.ServiceProvider };
            req.Request.Method = "GET";
            req.Request.Scheme = "https";
            req.Request.Host = new HostString("hanga.invalid");
            req.Request.Path = "/_content/Reports/css/report.css";
            var body = new MemoryStream();
            req.Response.Body = body;
            await pipeline!(req);
            string css = Encoding.UTF8.GetString(body.ToArray());
            Console.WriteLine($"static: {req.Response.StatusCode} {req.Response.ContentType} {css.Trim()}");
            if (req.Response.StatusCode != 200) failures++;
            await host.StopAsync();
            Console.WriteLine(failures == 0 ? "RESULT: OK" : "RESULT: FAILED " + failures);
            return failures;
        }
    }
}
