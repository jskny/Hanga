using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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
using Microsoft.Extensions.Primitives;
using PuppeteerSharp;

namespace InProc
{
    /// <summary>アプリのミドルウェアのパイプライン全体を捕まえる(Hangaが提供する想定の部品)。</summary>
    public sealed class PipelineCaptureStartupFilter : IStartupFilter
    {
        public static RequestDelegate? Pipeline;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // 最初に登録したミドルウェアの next は、アプリが Configure で組み立てたパイプライン全体になる
            app.Use(n => { Pipeline = n; return n; });
            next(app);
        };
    }

    public static class Program
    {
        public static string Chrome = "";
        private const string Origin = "https://hanga.invalid";

        public static void Main(string[] args)
        {
            Chrome = args[0];
            Host.CreateDefaultBuilder(args).ConfigureWebHostDefaults(w => w
                .UseUrls("http://127.0.0.1:5079")
                .ConfigureServices(s =>
                {
                    s.AddTransient<IStartupFilter, PipelineCaptureStartupFilter>();
                    s.AddControllersWithViews();
                    s.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
                    s.AddAuthorization();
                })
                .Configure(app =>
                {
                    app.UseStaticFiles();
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e =>
                    {
                        e.MapGet("/login", async ctx =>
                        {
                            var id = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, ctx.Request.Query["user"].ToString()) }, CookieAuthenticationDefaults.AuthenticationScheme);
                            await ctx.SignInAsync(new ClaimsPrincipal(id));
                            await ctx.Response.WriteAsync("logged in");
                        });
                        e.MapGet("/api/orders/{id}", async ctx =>
                        {
                            Console.WriteLine($"  [api] {ctx.Request.Path} user={ctx.User.Identity?.Name}");
                            await ctx.Response.WriteAsJsonAsync(new { orderNo = "ORD-" + ctx.Request.RouteValues["id"], customer = "株式会社サンプル（" + ctx.User.Identity?.Name + " が取得）", total = 1234567 });
                        }).RequireAuthorization();
                        e.MapGet("/order/pdf", OrderPdf).RequireAuthorization();
                        e.MapDefaultControllerRoute();
                    });
                })).Build().Run();
        }

        private static async Task<string> RenderViewAsync(HttpContext http, string controller, string view, object? model)
        {
            var routeData = new RouteData();
            routeData.Values["controller"] = controller;
            routeData.Values["action"] = view;
            var actionContext = new ActionContext(http, routeData, new ActionDescriptor());
            var engine = http.RequestServices.GetRequiredService<IRazorViewEngine>();
            var found = engine.FindView(actionContext, view, isMainPage: true);
            if (!found.Success) throw new InvalidOperationException("view not found");
            using var writer = new StringWriter();
            var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
            var tempData = new TempDataDictionary(http, http.RequestServices.GetRequiredService<ITempDataProvider>());
            await found.View.RenderAsync(new ViewContext(actionContext, found.View, viewData, tempData, writer, new HtmlHelperOptions()));
            return writer.ToString();
        }

        /// <summary>Chromiumからの要求を、アプリのパイプラインにプロセス内で渡し、応答を返す。元の要求のCookieを引き継ぐ。</summary>
        private static async Task<(int Status, string? ContentType, byte[] Body)> DispatchAsync(HttpContext original, IRequest req)
        {
            var uri = new Uri(req.Url);
            using var scope = original.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            ctx.Request.Method = req.Method.ToString().ToUpperInvariant();
            ctx.Request.Scheme = original.Request.Scheme;
            ctx.Request.Host = original.Request.Host;
            ctx.Request.PathBase = original.Request.PathBase;
            ctx.Request.Path = Uri.UnescapeDataString(uri.AbsolutePath);
            ctx.Request.QueryString = new QueryString(uri.Query);
            foreach (var h in req.Headers) ctx.Request.Headers[h.Key] = h.Value;
            // オペレーターの認証Cookieを引き継ぐ(Chromium側にはCookieが無い)
            if (original.Request.Headers.TryGetValue("Cookie", out StringValues cookie)) ctx.Request.Headers["Cookie"] = cookie;
            if (req.PostData != null) ctx.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(req.PostData.ToString()!));
            var body = new MemoryStream();
            ctx.Response.Body = body;
            await PipelineCaptureStartupFilter.Pipeline!(ctx);
            return (ctx.Response.StatusCode, ctx.Response.ContentType, body.ToArray());
        }

        private static async Task OrderPdf(HttpContext http)
        {
            string html = await RenderViewAsync(http, "Home", "Order", null);
            var problems = new List<string>();
            await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions { Headless = true, ExecutablePath = Chrome, Args = new[] { "--no-sandbox" } });
            await using var page = await browser.NewPageAsync();
            await page.SetRequestInterceptionAsync(true);
            page.Request += async (s, e) =>
            {
                var req = e.Request;
                if (!req.Url.StartsWith(Origin + "/", StringComparison.Ordinal)) { problems.Add("blocked: " + req.Url); await req.AbortAsync(); return; }
                if (new Uri(req.Url).AbsolutePath == "/__hanga/report")
                {
                    // 帳票のHTMLそのもの。ページのオリジンが仮想オリジンになり、相対URLのAPI呼び出しが同一オリジンになる
                    await req.RespondAsync(new ResponseData { Status = HttpStatusCode.OK, ContentType = "text/html; charset=utf-8", Body = html });
                    return;
                }
                try
                {
                    var (status, type, body) = await DispatchAsync(http, req);
                    Console.WriteLine($"  [dispatch] {req.Method} {new Uri(req.Url).PathAndQuery} -> {status} {type}");
                    if (status >= 400 || (status >= 300 && status < 400)) problems.Add($"HTTP {status}: {req.Url}");
                    await req.RespondAsync(new ResponseData { Status = (HttpStatusCode)status, ContentType = type, BodyData = body });
                }
                catch (Exception ex) { problems.Add("dispatch: " + ex.Message); await req.AbortAsync(); }
            };
            // Page.navigate(GoToAsync)は使わず、ページ内のJavaScriptで仮想オリジンへ移動する(18.1.0 の GoToAsync は新しいChromeで失敗するため)
            var navigation = page.WaitForNavigationAsync(new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.Networkidle0 }, Timeout = 30000 });
            await page.EvaluateExpressionAsync("location.href = '" + Origin + "/__hanga/report'");
            await navigation;
            await page.WaitForFunctionAsync("() => window.orderLoaded === true", new WaitForFunctionOptions { Timeout = 10000 });
            Console.WriteLine("  check: " + await page.EvaluateFunctionAsync<string>("() => document.getElementById('orderNo').textContent + ' / ' + document.getElementById('customer').textContent + ' / ' + document.getElementById('total').textContent"));
            foreach (var p in problems) Console.WriteLine("  PROBLEM " + p);
            var pdf = await page.PdfDataAsync(new PdfOptions { Width = "210mm", Height = "297mm", PrintBackground = true });
            http.Response.ContentType = "application/pdf";
            await http.Response.Body.WriteAsync(pdf, 0, pdf.Length);
        }
    }
}
