using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;

namespace Web
{
    public static class Program
    {
        public static string Chrome = "";

        public static void Main(string[] args)
        {
            Chrome = args[0];
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(w => w.UseUrls("http://127.0.0.1:5077").Configure(app =>
                {
                    app.UseStaticFiles();
                    app.UseRouting();
                    app.UseEndpoints(e =>
                    {
                        e.MapControllers();
                        e.MapGet("/pdf", async ctx =>
                        {
                            var log = ctx.RequestServices.GetRequiredService<ILogger<Startup>>();
                            log.LogInformation("runtime {V}, Extensions.Logging {L}, STJ {J}", Environment.Version,
                                typeof(ILogger).Assembly.GetName().Version, typeof(System.Text.Json.JsonSerializer).Assembly.GetName().Version);
                            var engine = new RazorLight.RazorLightEngineBuilder().UseEmbeddedResourcesProject(typeof(Program)).UseMemoryCachingProvider().Build();
                            string html = await engine.CompileRenderStringAsync("k", "<html><body><h1>@Model</h1></body></html>", "ASP.NET Core 5 内で生成");
                            await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions { Headless = true, ExecutablePath = Chrome, Args = new[] { "--no-sandbox" } });
                            await using var page = await browser.NewPageAsync();
                            await page.SetContentAsync(html);
                            var pdf = await page.PdfDataAsync();
                            ctx.Response.ContentType = "application/pdf";
                            await ctx.Response.Body.WriteAsync(pdf, 0, pdf.Length);
                        });
                    });
                }).ConfigureServices(s => s.AddControllers()))
                .Build().Run();
        }
    }

    public class Startup { }
}
