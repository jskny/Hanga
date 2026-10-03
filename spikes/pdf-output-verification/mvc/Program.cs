using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PuppeteerSharp;

namespace Mvc
{
    public sealed class NoopServer : Microsoft.AspNetCore.Hosting.Server.IServer
    {
        public Microsoft.AspNetCore.Http.Features.IFeatureCollection Features { get; } = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        public Task StartAsync<TContext>(Microsoft.AspNetCore.Hosting.Server.IHttpApplication<TContext> application, System.Threading.CancellationToken cancellationToken) where TContext : notnull => Task.CompletedTask;
        public Task StopAsync(System.Threading.CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }

    public static class Program
    {
        public static string Chrome = "";
        private const string Origin = "https://hanga.invalid";

        public static void Main(string[] args)
        {
            Chrome = args[0];
            if (args.Length > 1 && args[1] == "batch")
            {
                RunBatchAsync(args[2]).GetAwaiter().GetResult();
                return;
            }
            Host.CreateDefaultBuilder(args).ConfigureWebHostDefaults(w => w
                .UseUrls("http://127.0.0.1:5078")
                .ConfigureServices(s => s.AddControllersWithViews())
                .Configure(app =>
                {
                    app.UseStaticFiles();
                    app.UseRouting();
                    app.UseEndpoints(e =>
                    {
                        e.MapDefaultControllerRoute();
                        e.MapGet("/pdf", Pdf);
                    });
                })).Build().Run();
        }

        // バッチ: Webサーバー(Kestrel)を起動せず、HTTPリクエストも無い状態で、同じビューからPDFを作る
        private static async Task RunBatchAsync(string outputDir)
        {
            using var host = Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(w => w
                    .ConfigureServices(s =>
                    {
                        s.AddControllersWithViews();
                        // ポートを開かない「何もしないサーバー」に差し替える。ルーティングの登録などアプリの初期化だけを行わせる
                        s.AddSingleton<Microsoft.AspNetCore.Hosting.Server.IServer, NoopServer>();
                    })
                    .Configure(app => { app.UseRouting(); app.UseEndpoints(e => e.MapDefaultControllerRoute()); }))
                .Build();
            await host.StartAsync();
            Directory.CreateDirectory(outputDir);
            for (int i = 1; i <= 3; i++)
            {
                using var scope = host.Services.CreateScope();
                var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
                // エンドポイントルーティングでURLを生成させるため、ダミーのエンドポイントを設定する
                http.SetEndpoint(new Endpoint(null, EndpointMetadataCollection.Empty, "hanga-batch"));
                http.Request.Scheme = "https";
                http.Request.Host = new HostString("hanga.invalid");
                string path = Path.Combine(outputDir, "customer-" + i + ".pdf");
                byte[] pdf = await RenderPdfAsync(http);
                await File.WriteAllBytesAsync(path, pdf);
                Console.WriteLine("batch: " + path + " " + pdf.Length + " bytes");
            }
            await host.StopAsync();
        }

        // コントローラーの外で、アプリ自身のビューエンジンを使ってビューをHTML文字列にする
        private static async Task<string> RenderViewAsync(HttpContext http, string controller, string view, object? model)
        {
            var routeData = new RouteData();
            routeData.Values["controller"] = controller;
            routeData.Values["action"] = view;
            var actionContext = new ActionContext(http, routeData, new ActionDescriptor());
            var engine = http.RequestServices.GetRequiredService<IRazorViewEngine>();
            var found = engine.FindView(actionContext, view, isMainPage: true);
            if (!found.Success) throw new InvalidOperationException("view not found: " + string.Join(", ", found.SearchedLocations));
            using var writer = new StringWriter();
            var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
            var tempData = new TempDataDictionary(http, http.RequestServices.GetRequiredService<ITempDataProvider>());
            var ctx = new ViewContext(actionContext, found.View, viewData, tempData, writer, new HtmlHelperOptions());
            await found.View.RenderAsync(ctx);
            return writer.ToString();
        }

        private static async Task Pdf(HttpContext http)
        {
            var pdf = await RenderPdfAsync(http);
            http.Response.ContentType = "application/pdf";
            await http.Response.Body.WriteAsync(pdf, 0, pdf.Length);
        }

        private static async Task<byte[]> RenderPdfAsync(HttpContext http)
        {
            string html = await RenderViewAsync(http, "Home", "Index", null);
            Console.WriteLine("----- rendered html -----\n" + html + "\n-------------------------");
            string root = Path.GetFullPath(http.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootPath);
            var problems = new List<string>();
            await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions { Headless = true, ExecutablePath = Chrome, Args = new[] { "--no-sandbox" } });
            await using var page = await browser.NewPageAsync();
            await page.SetRequestInterceptionAsync(true);
            page.Request += async (s, e) =>
            {
                var req = e.Request;
                var uri = new Uri(req.Url);
                if (!req.Url.StartsWith(Origin + "/", StringComparison.Ordinal)) { problems.Add("blocked: " + req.Url); await req.AbortAsync(); return; }
                string full = Path.GetFullPath(Path.Combine(root, Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/')));
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full)) { problems.Add("not found: " + req.Url); await req.RespondAsync(new ResponseData { Status = HttpStatusCode.NotFound, Body = "" }); return; }
                string type = full.EndsWith(".css") ? "text/css" : full.EndsWith(".js") ? "text/javascript" : "application/octet-stream";
                await req.RespondAsync(new ResponseData { Status = HttpStatusCode.OK, ContentType = type, BodyData = File.ReadAllBytes(full) });
            };
            await page.SetContentAsync(html.Replace("<head>", "<head><base href=\"" + Origin + "/\">"), new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.Networkidle0 } });
            await page.WaitForFunctionAsync("() => window.hangaReady === true", new WaitForFunctionOptions { Timeout = 10000 });
            string check = await page.EvaluateFunctionAsync<string>("() => getComputedStyle(document.querySelector('h1')).color + ' / ' + document.getElementById('js').textContent");
            Console.WriteLine("check: " + check);
            foreach (var p in problems) Console.WriteLine("PROBLEM " + p);
            return await page.PdfDataAsync(new PdfOptions { Width = "210mm", Height = "297mm", PrintBackground = true });
        }
    }
}
