using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using PuppeteerSharp;
using PuppeteerSharp.Media;
using RazorLight;

namespace Poc
{
    public class InvoiceLine
    {
        public string Name { get; set; } = "";
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
    }

    public class InvoiceModel
    {
        public string CustomerName { get; set; } = "";
        public string InvoiceNo { get; set; } = "";
        public List<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();
    }

    public static class Program
    {
        // 仮想のオリジン。このオリジンへの要求はすべてHangaが応答し、実際の通信は発生しない
        private const string Origin = "https://hanga.invalid";

        public static async Task<int> Main(string[] args)
        {
            string chrome = args[0];
            string output = args[1];
            string baseDir = AppContext.BaseDirectory;
            string root = Path.GetFullPath(Path.Combine(baseDir, "wwwroot"));

            // ① CSHTML → HTML
            var sw = Stopwatch.StartNew();
            var engine = new RazorLightEngineBuilder()
                .UseFileSystemProject(Path.Combine(baseDir, "templates"))
                .UseMemoryCachingProvider()
                .Build();
            var model = new InvoiceModel { CustomerName = "株式会社サンプル 御中", InvoiceNo = "INV-0001" };
            for (int i = 1; i <= 60; i++)
            {
                model.Lines.Add(new InvoiceLine { Name = "商品" + i + "（髙﨑・𠮷野家）", Quantity = i % 7 + 1, UnitPrice = 1234.5m * i });
            }
            string html = await engine.CompileRenderAsync("invoice.cshtml", model);
            Console.WriteLine($"razor: {sw.ElapsedMilliseconds} ms, html {html.Length} chars");

            // ② HTML → PDF
            var served = new List<string>();
            var problems = new List<string>();
            sw.Restart();
            var launch = new LaunchOptions
            {
                Headless = true,
                ExecutablePath = chrome,
                Args = new[] { "--no-sandbox" },
            };
            await using var browser = await Puppeteer.LaunchAsync(launch);
            Console.WriteLine($"browser: {await browser.GetVersionAsync()} (launch {sw.ElapsedMilliseconds} ms)");
            await using var page = await browser.NewPageAsync();
            page.Console += (s, e) => Console.WriteLine($"  console[{e.Message.Type}]: {e.Message.Text}");
            page.PageError += (s, e) => problems.Add("pageerror: " + e.Message);
            await page.SetRequestInterceptionAsync(true);
            page.Request += async (s, e) =>
            {
                var req = e.Request;
                try
                {
                    var uri = new Uri(req.Url);
                    if (!req.Url.StartsWith(Origin + "/", StringComparison.Ordinal))
                    {
                        problems.Add("blocked: " + req.Url);
                        await req.AbortAsync();
                        return;
                    }
                    if (uri.AbsolutePath == "/__hanga/gaiji.ttf" && Environment.GetEnvironmentVariable("HANGA_GAIJI_FONT") is string gaiji)
                    {
                        served.Add("__hanga/gaiji.ttf");
                        await req.RespondAsync(new ResponseData { Status = HttpStatusCode.OK, ContentType = "font/ttf", BodyData = File.ReadAllBytes(gaiji), Headers = new Dictionary<string, object> { ["Access-Control-Allow-Origin"] = "*" } });
                        return;
                    }
                    if (uri.AbsolutePath == "/__report")
                    {
                        await req.RespondAsync(new ResponseData { Status = HttpStatusCode.OK, ContentType = "text/html; charset=utf-8", Body = html });
                        return;
                    }
                    string rel = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
                    string full = Path.GetFullPath(Path.Combine(root, rel));
                    if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
                    {
                        problems.Add("not found: " + req.Url);
                        await req.RespondAsync(new ResponseData { Status = HttpStatusCode.NotFound, Body = "" });
                        return;
                    }
                    served.Add(rel);
                    await req.RespondAsync(new ResponseData { Status = HttpStatusCode.OK, ContentType = ContentType(full), BodyData = File.ReadAllBytes(full), Headers = new Dictionary<string, object> { ["Access-Control-Allow-Origin"] = "*" } });
                }
                catch (Exception ex)
                {
                    problems.Add("handler: " + ex.Message);
                }
            };

            sw.Restart();
            string mode = args.Length > 2 ? args[2] : "navigate";
            Console.WriteLine("mode: " + mode);
            if (mode == "setcontent")
            {
                // Page.navigate を使わず、<base> で相対URLの基準を仮想オリジンに向けてからHTMLを流し込む
                string inject = "<base href=\"" + Origin + "/\">";
                if (Environment.GetEnvironmentVariable("HANGA_GAIJI_FONT") != null)
                {
                    // 外字用フォント。HANGA_GAIJI_SRC=local なら、サーバーにインストール済みのフォントを名前で参照する(ファイルを転送しない)
                    string src = Environment.GetEnvironmentVariable("HANGA_GAIJI_SRC") == "local"
                        ? "local('IPAmj明朝'),local('IPAmjMincho')"
                        : "url('/__hanga/gaiji.ttf') format('truetype')";
                    // unicode-range で、通常のフォントに無いことが多い範囲(CJK拡張B以降・私用領域)だけに使わせる。該当する文字が無いページではフォントを読み込まない
                    inject += "<style>@font-face{font-family:'HangaGaiji';src:" + src + ";unicode-range:U+20000-3134F,U+E000-F8FF,U+F0000-10FFFF;}"
                        + "@font-face{font-family:'HangaGaijiIvs';src:" + src + ";}"
                        + ".hanga-ivs{font-family:'HangaGaijiIvs' !important;}</style>"
                        + "<script>document.addEventListener('DOMContentLoaded',function(){"
                        // 異体字セレクタ付きの文字(基底文字+U+E0100〜E01EF)を、外字用フォントで描く span で包む
                        + "var re=/[\\s\\S](?:\\uDB40[\\uDD00-\\uDDEF])/g;var w=document.createTreeWalker(document.body,NodeFilter.SHOW_TEXT),n,list=[];while(n=w.nextNode()){if(re.test(n.nodeValue))list.push(n);re.lastIndex=0;}"
                        + "list.forEach(function(t){var f=document.createDocumentFragment(),s=t.nodeValue,i=0,m;re.lastIndex=0;while(m=re.exec(s)){f.appendChild(document.createTextNode(s.slice(i,m.index)));var sp=document.createElement('span');sp.className='hanga-ivs';sp.textContent=m[0];f.appendChild(sp);i=m.index+m[0].length;}f.appendChild(document.createTextNode(s.slice(i)));t.parentNode.replaceChild(f,t);});"
                        // 全要素のフォント指定の末尾に外字用フォントを足す(元のフォントに字形が無い文字だけがこのフォントで描かれる)
                        + "document.querySelectorAll('*').forEach(function(el){var f=getComputedStyle(el).fontFamily;if(f.indexOf('HangaGaiji')<0){el.style.fontFamily=f+\",'HangaGaiji'\";}});"
                        + "});</script>";
                }
                string withBase = html.Replace("<head>", "<head>" + inject);
                await page.SetContentAsync(withBase, new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.Networkidle0 }, Timeout = 30000 });
            }
            else
            {
                await page.GoToAsync(Origin + "/__report", new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.Networkidle0 }, Timeout = 30000 });
            }
            await page.WaitForFunctionAsync("() => window.hangaReady === true", new WaitForFunctionOptions { Timeout = 10000 });
            await page.EvaluateFunctionAsync("() => document.fonts.ready.then(() => true)");
            string check = await page.EvaluateFunctionAsync<string>("() => document.getElementById('total').textContent + ' / ' + getComputedStyle(document.body).fontFamily + ' / font loaded=' + document.fonts.check('12px HangaWebFont')");
            Console.WriteLine($"page ready: {sw.ElapsedMilliseconds} ms; {check}");

            sw.Restart();
            await page.PdfAsync(output, new PdfOptions
            {
                Format = PaperFormat.A4,
                PrintBackground = true,
                DisplayHeaderFooter = true,
                HeaderTemplate = "<span></span>",
                FooterTemplate = "<div style='font-size:9px;width:100%;text-align:center'><span class='pageNumber'></span> / <span class='totalPages'></span></div>",
                MarginOptions = new MarginOptions { Top = "15mm", Bottom = "15mm", Left = "12mm", Right = "12mm" },
            });
            Console.WriteLine($"pdf: {sw.ElapsedMilliseconds} ms, {new FileInfo(output).Length} bytes");
            Console.WriteLine("served: " + string.Join(", ", served.Distinct()));
            foreach (var p in problems) Console.WriteLine("PROBLEM " + p);
            return 0;
        }

        private static string ContentType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".css": return "text/css; charset=utf-8";
                case ".js": return "text/javascript; charset=utf-8";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".ttf": return "font/ttf";
                case ".woff2": return "font/woff2";
                default: return "application/octet-stream";
            }
        }
    }
}
