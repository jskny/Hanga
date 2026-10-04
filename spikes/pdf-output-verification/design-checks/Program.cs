using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace DCheck
{
    /// <summary>design.md の作成前に確認する項目をまとめて検証する。</summary>
    public static class Program
    {
        private const string Origin = "https://hanga.invalid";
        private static string outDir = "";

        // 外字用フォントの注入(インストール済みの IPAmj明朝 を名前で参照)と、異体字の包み込み
        private const string GaijiInject =
            "<style>@font-face{font-family:'HangaGaiji';src:local('IPAmj明朝'),local('IPAmjMincho');unicode-range:U+20000-3134F,U+E000-F8FF,U+F0000-10FFFF;}"
            + "@font-face{font-family:'HangaGaijiIvs';src:local('IPAmj明朝'),local('IPAmjMincho');}.hanga-ivs{font-family:'HangaGaijiIvs' !important;}</style>"
            + "<script>document.addEventListener('DOMContentLoaded',function(){"
            + "var re=/[\\s\\S](?:\\uDB40[\\uDD00-\\uDDEF])/g;var w=document.createTreeWalker(document.body,NodeFilter.SHOW_TEXT),n,list=[];while(n=w.nextNode()){re.lastIndex=0;if(re.test(n.nodeValue))list.push(n);}"
            + "list.forEach(function(t){var f=document.createDocumentFragment(),s=t.nodeValue,i=0,m;re.lastIndex=0;while(m=re.exec(s)){f.appendChild(document.createTextNode(s.slice(i,m.index)));var sp=document.createElement('span');sp.className='hanga-ivs';sp.textContent=m[0];f.appendChild(sp);i=m.index+m[0].length;}f.appendChild(document.createTextNode(s.slice(i)));t.parentNode.replaceChild(f,t);});"
            + "document.querySelectorAll('*').forEach(function(el){var f=getComputedStyle(el).fontFamily;if(f.indexOf('HangaGaiji')<0){el.style.fontFamily=f+\",'HangaGaiji'\";}});"
            + "});</script>";

        // 字形の無い文字を探す: 判定用フォントA・B(全符号位置に、形の違う字形を持つ)を、要素のフォント指定の末尾に足して canvas に描き比べる。
        // 結果が一致すれば要素のフォント(外字用フォントを含む)で描かれており、異なれば判定用フォントまで落ちた = 字形が無い。
        // OSの代替フォントは、判定用フォントより後にしか使われないため、判定に影響しない。
        private const string FindMissingGlyphs = @"async () => {
            await Promise.all([document.fonts.load('40px HangaProbeA'), document.fonts.load('40px HangaProbeB')]);
            const c = document.createElement('canvas'); c.width = 64; c.height = 64;
            const g = c.getContext('2d', { willReadFrequently: true });
            const draw = (font, ch) => { g.clearRect(0, 0, 64, 64); g.font = font; g.fillText(ch, 4, 48); return g.getImageData(0, 0, 64, 64).data; };
            const same = (a, b) => { for (let i = 3; i < a.length; i += 4) if (a[i] !== b[i]) return false; return true; };
            const missing = new Set(); const seen = new Set();
            const w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT); let n;
            while ((n = w.nextNode())) {
                const el = n.parentElement; if (!el) continue;
                const cs = getComputedStyle(el); if (cs.display === 'none' || cs.visibility === 'hidden') continue;
                const fam = cs.fontFamily;
                for (const ch of Array.from(n.nodeValue)) {
                    if (/\s/.test(ch) || /[\uFE00-\uFE0F]|[\u{E0100}-\u{E01EF}]/u.test(ch) || seen.has(fam + ch)) continue; seen.add(fam + ch);
                    if (!same(draw('40px ' + fam + ', HangaProbeA', ch), draw('40px ' + fam + ', HangaProbeB', ch))) missing.add(ch);
                }
            }
            return Array.from(missing);
        }";

        private static string ProbeFonts(string dir) =>
            "<style>@font-face{font-family:'HangaProbeA';src:url(data:font/ttf;base64," + Convert.ToBase64String(System.IO.File.ReadAllBytes(dir + "/probe-a.ttf")) + ");}"
            + "@font-face{font-family:'HangaProbeB';src:url(data:font/ttf;base64," + Convert.ToBase64String(System.IO.File.ReadAllBytes(dir + "/probe-b.ttf")) + ");}</style>";

        public static async Task Main(string[] args)
        {
            string chrome = args[0];
            outDir = args[1];
            System.IO.Directory.CreateDirectory(outDir);
            var sw = Stopwatch.StartNew();
            await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions { Headless = true, ExecutablePath = chrome, Args = new[] { "--no-sandbox" } });
            Console.WriteLine($"[launch] {await browser.GetVersionAsync()} {sw.ElapsedMilliseconds} ms");

            if (args.Length > 2 && args[2] == "glyphdebug")
            {
                var p = await OpenAsync(browser, "<html><body><p style='font-family:IPAGothic'>A</p></body></html>");
                var r = await p.Page.EvaluateFunctionAsync<string>(@"() => {
                    const c = document.createElement('canvas'); c.width = 64; c.height = 64; const g = c.getContext('2d');
                    const ink = ch => { g.clearRect(0,0,64,64); g.font = '40px IPAGothic'; g.fillText(ch, 4, 48); const d = g.getImageData(0,0,64,64).data; let n = 0, h = 0; for (let i = 3; i < d.length; i += 4) { if (d[i]) n++; h = (h * 31 + d[i]) | 0; } return ch.codePointAt(0).toString(16) + ':' + n + ':' + h + ':w' + g.measureText(ch).width.toFixed(1); };
                    return ['͸','͹','A','髙','𠮷',''].map(ink).join(' ');
                }");
                Console.WriteLine("[glyphdebug] " + r);
                return;
            }
            await CheckContextIsolation(browser);
            await CheckMedia(browser);
            await CheckFitToWidthAndSinglePage(browser);
            await CheckMissingGlyphs(browser);
            await CheckIvs(browser);
            await CheckReuseTiming(browser);
        }

        /// <summary>帳票1件ごとのブラウザコンテキストを作り、仮想オリジンでHTMLを表示したページを返す。</summary>
        private static async Task<(IBrowserContext Ctx, IPage Page)> OpenAsync(IBrowser browser, string html, bool incognito = true, IBrowserContext? shared = null)
        {
            var ctx = shared ?? (incognito ? await browser.CreateBrowserContextAsync() : browser.DefaultContext);
            var page = await ctx.NewPageAsync();
            await page.SetRequestInterceptionAsync(true);
            page.Request += async (s, e) =>
            {
                if (e.Request.Url == Origin + "/__hanga/report")
                    await e.Request.RespondAsync(new ResponseData { Status = HttpStatusCode.OK, ContentType = "text/html; charset=utf-8", Body = html });
                else
                    await e.Request.RespondAsync(new ResponseData { Status = HttpStatusCode.NoContent, Body = "" });
            };
            var nav = page.WaitForNavigationAsync(new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.Networkidle0 } });
            await page.EvaluateExpressionAsync("location.href = '" + Origin + "/__hanga/report'");
            await nav;
            await page.EvaluateFunctionAsync("() => document.fonts.ready.then(() => true)");
            return (ctx, page);
        }

        private static async Task CheckContextIsolation(IBrowser browser)
        {
            const string html = "<html><body>ctx</body></html>";
            // シークレットのコンテキストを帳票ごとに作る場合
            var a = await OpenAsync(browser, html);
            await a.Page.EvaluateExpressionAsync("localStorage.setItem('secret', 'operator-A')");
            var b = await OpenAsync(browser, html);
            var seenB = await b.Page.EvaluateExpressionAsync<string?>("localStorage.getItem('secret')");
            await a.Ctx.CloseAsync(); await b.Ctx.CloseAsync();
            // 既定のコンテキストを使い回した場合
            var c = await OpenAsync(browser, html, incognito: false);
            await c.Page.EvaluateExpressionAsync("localStorage.setItem('secret', 'operator-C')");
            await c.Page.CloseAsync();
            var d = await OpenAsync(browser, html, incognito: false);
            var seenD = await d.Page.EvaluateExpressionAsync<string?>("localStorage.getItem('secret')");
            await d.Page.CloseAsync();
            Console.WriteLine($"[isolation] incognito per report: other report sees '{seenB ?? "(null)"}' / shared default context: other report sees '{seenD ?? "(null)"}'");
        }

        private static async Task CheckMedia(IBrowser browser)
        {
            const string html = "<html><head><style>.print-only{display:none}@media print{.print-only{display:block}.screen-only{display:none}}</style></head>"
                + "<body><p class='screen-only'>SCREEN-ONLY-TEXT</p><p class='print-only'>PRINT-ONLY-TEXT</p><p>COMMON-TEXT</p></body></html>";
            var p = await OpenAsync(browser, html);
            await p.Page.PdfAsync(outDir + "/media-print.pdf", A4());
            await p.Page.EmulateMediaTypeAsync(MediaType.Screen);
            await p.Page.PdfAsync(outDir + "/media-screen.pdf", A4());
            await p.Ctx.CloseAsync();
            Console.WriteLine("[media] wrote media-print.pdf / media-screen.pdf");
        }

        private static PdfOptions A4(decimal scale = 1m) => new PdfOptions
        {
            Width = "210mm", Height = "297mm", PrintBackground = true, Scale = scale,
            MarginOptions = new MarginOptions { Top = "10mm", Bottom = "10mm", Left = "10mm", Right = "10mm" },
        };

        private static async Task CheckFitToWidthAndSinglePage(IBrowser browser)
        {
            var sb = new StringBuilder("<html><head><style>body{margin:0;font-family:IPAGothic}table{border-collapse:collapse;width:1800px}td{border:1px solid #333;padding:2px}</style></head><body><table>");
            for (int r = 1; r <= 120; r++) sb.Append($"<tr><td>ROW{r}-LEFT</td><td style='width:1500px'>中央</td><td>ROW{r}-RIGHT-EDGE</td></tr>");
            sb.Append("</table></body></html>");
            var p = await OpenAsync(browser, sb.ToString());
            // 印刷時の幅で測るため、印刷用の表示に切り替えてから測る
            await p.Page.EmulateMediaTypeAsync(MediaType.Print);
            var size = await p.Page.EvaluateFunctionAsync<double[]>("() => [document.documentElement.scrollWidth, document.documentElement.scrollHeight]");
            double printableWidthPx = (210 - 20) / 25.4 * 96;
            decimal scale = (decimal)Math.Max(0.1, Math.Min(1.0, printableWidthPx / size[0]));
            await p.Page.PdfAsync(outDir + "/wide-noscale.pdf", A4());
            await p.Page.PdfAsync(outDir + "/wide-fit.pdf", A4(scale));
            // 全体を1ページ: 縮小後の内容の高さ + 余白 をページの高さにする
            double heightMm = size[1] * (double)scale / 96 * 25.4 + 20 + 1;
            var single = A4(scale); single.Height = heightMm.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "mm";
            await p.Page.PdfAsync(outDir + "/wide-single.pdf", single);
            await p.Ctx.CloseAsync();
            Console.WriteLine($"[fit] content {size[0]}x{size[1]} px, printable width {printableWidthPx:0} px, scale {scale:0.000}, single page height {heightMm:0} mm");
        }

        private static async Task CheckMissingGlyphs(IBrowser browser)
        {
            const string body = "<p style='font-family:IPAGothic'>通常の文字 ABC 髙﨑 𠮷野家</p><p style='font-family:IPAGothic'>私用領域  と 未割り当て ͹</p>";
            string probe = ProbeFonts(AppContext.BaseDirectory + "/..");
            var plain = await OpenAsync(browser, "<html><head>" + probe + "</head><body>" + body + "</body></html>");
            var m1 = await plain.Page.EvaluateFunctionAsync<string[]>(FindMissingGlyphs);
            await plain.Ctx.CloseAsync();
            var gaiji = await OpenAsync(browser, "<html><head>" + probe + GaijiInject + "</head><body>" + body + "</body></html>");
            var m2 = await gaiji.Page.EvaluateFunctionAsync<string[]>(FindMissingGlyphs);
            await gaiji.Page.PdfAsync(outDir + "/glyph-gaiji.pdf", A4());
            await gaiji.Ctx.CloseAsync();
            Console.WriteLine("[glyph] without gaiji font: " + Describe(m1) + " / with gaiji font: " + Describe(m2));
        }

        private static string Describe(string[] chars) => chars.Length == 0 ? "(none)" : string.Join(" ", chars.Select(c => c + "(" + string.Join("+", EnumerateCodePoints(c).Select(cp => "U+" + cp.ToString("X4"))) + ")"));

        private static IEnumerable<int> EnumerateCodePoints(string s)
        {
            for (int i = 0; i < s.Length; i += char.IsSurrogatePair(s, i) ? 2 : 1) yield return char.ConvertToUtf32(s, i);
        }

        private static async Task CheckIvs(IBrowser browser)
        {
            // 葛+E0102・辻+E0102 は IPAmj明朝で既定と異なる字形。葛+E0100 は既定と同じ字形(対照)
            string Span(string id, string text) => $"<span id='{id}' style='font-size:64px;font-family:IPAGothic'>{text}</span> ";
            string body = Span("k0", "葛") + Span("k2", "葛\U000E0102") + Span("k0b", "葛\U000E0100") + Span("t0", "辻") + Span("t2", "辻\U000E0102");
            var p = await OpenAsync(browser, "<html><head>" + GaijiInject + "</head><body>" + body + "</body></html>");
            var shots = new Dictionary<string, string>();
            foreach (var id in new[] { "k0", "k2", "k0b", "t0", "t2" })
            {
                var el = await p.Page.QuerySelectorAsync("#" + id);
                shots[id] = Convert.ToBase64String(await el.ScreenshotDataAsync());
            }
            var families = await p.Page.EvaluateFunctionAsync<string[]>("() => ['k0','k2','t2'].map(id => { const s = document.querySelector('#' + id + ' .hanga-ivs') || document.getElementById(id); return id + ':' + getComputedStyle(s).fontFamily; })");
            await p.Page.PdfAsync(outDir + "/ivs.pdf", A4());
            await p.Ctx.CloseAsync();
            Console.WriteLine($"[ivs] 葛 vs 葛+E0102 differ: {shots["k0"] != shots["k2"]} / 葛+E0100 vs 葛+E0102 differ: {shots["k0b"] != shots["k2"]} / 辻 vs 辻+E0102 differ: {shots["t0"] != shots["t2"]}");
            Console.WriteLine("[ivs] fonts: " + string.Join(" | ", families));
        }

        private static async Task CheckReuseTiming(IBrowser browser)
        {
            var sb = new StringBuilder("<html><head><style>body{font-family:IPAGothic}</style></head><body><h1>請求書</h1><table>");
            for (int r = 1; r <= 60; r++) sb.Append($"<tr><td>商品{r}</td><td>{r * 1000}</td></tr>");
            sb.Append("</table></body></html>");
            var times = new List<long>();
            for (int i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                var p = await OpenAsync(browser, sb.ToString());
                await p.Page.PdfDataAsync(A4());
                await p.Ctx.CloseAsync();
                times.Add(sw.ElapsedMilliseconds);
            }
            var parallel = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ => { var p = await OpenAsync(browser, sb.ToString()); await p.Page.PdfDataAsync(A4()); await p.Ctx.CloseAsync(); }));
            Console.WriteLine($"[timing] reused browser, sequential per report: {string.Join(", ", times)} ms / 8 in parallel: {parallel.ElapsedMilliseconds} ms total");
        }
    }
}
