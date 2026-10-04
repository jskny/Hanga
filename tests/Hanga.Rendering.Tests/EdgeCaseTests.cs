using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Hanga.TestSupport;
using Xunit;

namespace Hanga.Rendering.Tests
{
    /// <summary>
    /// エッジケースの検証(samples/Hanga.EdgeCases)とそのレビューで見つかった問題の回帰テスト(view-pdf-generation の要件2.7, 3.4, 4.6, 5.5, 8.6, 8.7)。
    /// Chromium を使う。
    /// </summary>
    public class EdgeCaseTests
    {
        [Theory]
        [InlineData("alert('お知らせ'); document.getElementById('v').textContent = 'ダイアログの後';", "ダイアログの後")]
        [InlineData("document.getElementById('v').textContent = 'ダイアログの後 ' + confirm('削除しますか');", "ダイアログの後 false")]
        [InlineData("document.getElementById('v').textContent = 'ダイアログの後 ' + prompt('名前', '既定値');", "ダイアログの後 null")]
        public async Task ダイアログで止まらず確認はキャンセルで閉じて警告にする(string script, string expected)
        {
            // 要件4.6: 閉じないとページの JavaScript が止まり、タイムアウトになっていた。
            // 確認(confirm・prompt)は「キャンセル」で閉じる(誰も答えていない確認の後の処理を実行させない)
            var result = await Render("<p id='v'>ダイアログの前</p><script>" + script + "</script>");

            Assert.Contains(expected, PdfInspector.Read(result.Pdf).AllText);
            Assert.Contains(result.Warnings, w => w.Kind == HangaWarningKind.Dialog);
        }

        [Fact]
        public async Task 確認の後の削除の要求はアプリに届かない()
        {
            // セキュリティレビューの再現: 「OK」で閉じると、オペレーターの権限で削除の API が呼ばれていた
            var received = new ConcurrentBag<string>();
            var handler = new FakeVirtualOriginHandler().On("/api/delete", r =>
            {
                received.Add(r.Method);
                return new VirtualResponse(200, "application/json", new byte[0]);
            });
            await ReportRendererWaitTests.RenderAsync(
                ReportRendererWaitTests.Page("<p>本文</p><script>if (confirm('削除しますか')) { fetch('/api/delete', { method: 'POST' }); }</script>"), handler);

            Assert.Empty(received);
        }

        [Fact]
        public async Task ダイアログは厳格な扱いではエラーにする()
        {
            await Assert.ThrowsAsync<HangaStrictModeException>(() => Render("<p>本文</p><script>alert('x');</script>", o => o.Strict = true));
        }

        [Fact]
        public async Task 警告は種類ごとに上限までにし詳細を切り詰める()
        {
            // 繰り返しダイアログを出すページで、警告とログが際限なく増えていた
            var result = await Render("<p>本文</p><script>for (var i = 0; i < 50; i++) { alert('x'.repeat(1000)); }</script>");

            var dialogs = result.Warnings.Where(w => w.Kind == HangaWarningKind.Dialog).ToList();
            Assert.Equal(ReportPage.MaxWarningsPerKind + 1, dialogs.Count);
            Assert.Contains("30 件", dialogs.Last().Message);
            Assert.All(dialogs.Where(w => w.Detail != null), w => Assert.InRange(w.Detail!.Length, 0, ReportPage.MaxWarningDetailLength + 1));
        }

        [Theory]
        [InlineData("setTimeout(function () { location.href = '/other'; }, 100);")]
        [InlineData("setTimeout(function () { location.reload(); }, 100);")]
        [InlineData("setTimeout(function () { document.getElementById('f').submit(); }, 100);")]
        [InlineData("var m = document.createElement('meta'); m.httpEquiv = 'refresh'; m.content = '1;url=/other'; document.head.appendChild(m);")]
        [InlineData("var m = document.createElement('meta'); m.httpEquiv = 'refresh'; m.content = '0;url=/other'; document.head.appendChild(m);")]
        [InlineData("location.href = '/other';")]
        [InlineData("document.getElementById('f').submit();")]
        public async Task 開いた後のページの移動を止めて元のページをPDFにする(string script)
        {
            // 要件2.7: 止めないと、移動先(ここではアプリの別のページ)の内容の PDF を黙って返していた
            var handler = new FakeVirtualOriginHandler().Text("/other", "text/html; charset=utf-8", "<p>移動先のページ</p>");
            var result = await ReportRendererWaitTests.RenderAsync(
                ReportRendererWaitTests.Page("<p>元のページ</p><form id='f' method='post' action='/other'></form><script>" + script + "</script>"), handler);

            string text = PdfInspector.Read(result.Pdf).AllText;
            Assert.Contains("元のページ", text);
            Assert.DoesNotContain("移動先のページ", text);
            Assert.Contains(result.Warnings, w => w.Kind == HangaWarningKind.BlockedNavigation);
        }

        [Fact]
        public async Task ページの移動は厳格な扱いではエラーにする()
        {
            await Assert.ThrowsAsync<HangaStrictModeException>(() => Render(
                "<p>元のページ</p><script>setTimeout(function () { location.href = '/other'; }, 100);</script>", o => o.Strict = true));
        }

        [Fact]
        public async Task 要求を出さない移動で帳票のページから離れたらエラーにする()
        {
            // about:blank への移動は要求への介入で止められない。白紙の PDF を黙って返していた
            var ex = await Assert.ThrowsAsync<HangaBrowserException>(() => Render(
                "<p>元のページ</p><script>setTimeout(function () { location.href = 'about:blank'; }, 100);</script>"));
            Assert.Contains("別のページへ移動", ex.Message);
        }

        [Fact]
        public async Task 内側のフレームは表示しフォームの送信は止める()
        {
            var received = new ConcurrentBag<string>();
            var handler = new FakeVirtualOriginHandler()
                .Text("/frame", "text/html; charset=utf-8", "<p>フレームの中</p><form id='f' method='post' action='/api/delete'></form><script>document.getElementById('f').submit();</script>")
                .On("/api/delete", r =>
                {
                    received.Add(r.Method);
                    return new VirtualResponse(200, "text/html", new byte[0]);
                });
            var result = await ReportRendererWaitTests.RenderAsync(ReportRendererWaitTests.Page("<p>元のページ</p><iframe src='/frame'></iframe>"), handler);

            Assert.Contains("フレームの中", PdfInspector.Read(result.Pdf).AllText);
            Assert.Empty(received);
            Assert.Contains(result.Warnings, w => w.Kind == HangaWarningKind.BlockedNavigation);
        }

        [Fact]
        public async Task 印刷中に起きた問題も厳格な扱いではエラーにする()
        {
            // Chromium は PDF の出力の際に beforeprint を発火する。その間の警告を見逃していた
            await Assert.ThrowsAsync<HangaStrictModeException>(() => Render(
                "<p>本文</p><script>window.addEventListener('beforeprint', function () { throw new Error('印刷の前の誤り'); });</script>", o => o.Strict = true));
        }

        [Fact]
        public async Task 別のウィンドウやWebSocketで許可していない宛先へ通信できない()
        {
            // 要件3.4(セキュリティレビューの再現): 要求への介入が効かない別のウィンドウ・WebSocket で、外部へ値を送れていた
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string target = "http://127.0.0.1:" + port;
                var result = await Render(
                    "<p>本文</p><a id='a' target='_blank' href='" + target + "/a?d=secret'>a</a>"
                    + "<script>window.open('" + target + "/open?d=secret'); document.getElementById('a').click();"
                    + "try { new WebSocket('ws://127.0.0.1:" + port + "/ws'); } catch (e) { }</script>");
                await Task.Delay(1000);

                var lines = new System.Collections.Generic.List<string>();
                while (listener.Pending())
                {
                    using var client = listener.AcceptTcpClient();
                    client.ReceiveTimeout = 1000;
                    var buffer = new byte[200];
                    int n = 0;
                    try { n = client.GetStream().Read(buffer, 0, buffer.Length); } catch (System.IO.IOException) { }
                    lines.Add(System.Text.Encoding.ASCII.GetString(buffer, 0, n).Split('\n')[0]);
                }

                Assert.True(lines.Count == 0, "許可していない宛先に接続が届いた: " + string.Join(" / ", lines));
                Assert.Contains(result.Warnings, w => w.Kind == HangaWarningKind.BlockedNavigation);
            }
            finally
            {
                listener.Stop();
            }
        }

        [Fact]
        public async Task 一ページ化で用紙の高さの上限を超えたら警告にし厳格な扱いではエラーにする()
        {
            // 要件5.5: 上限(5000mm)を超えた分は次のページになる。黙って複数ページにしていた
            string html = string.Concat(Enumerable.Range(1, 2500).Select(i => "<div style='height:24px'>行 " + i + "</div>")) + "<p>終わり</p>";
            var result = await Render(html, o => o.SinglePage = true);

            var pdf = PdfInspector.Read(result.Pdf);
            Assert.True(pdf.PageCount > 1);
            Assert.Contains("終わり", pdf.AllText);
            Assert.Contains(result.Warnings, w => w.Kind == HangaWarningKind.SinglePageOverflow);

            await Assert.ThrowsAsync<HangaStrictModeException>(() => Render(html, o => { o.SinglePage = true; o.Strict = true; }));
        }

        [Fact]
        public void 許可したホスト以外への通信を行き止まりのプロキシに向ける()
        {
            var options = new HangaOptions { ChromiumExecutablePath = "chrome" };
            options.AllowedExternalHosts.Add("cdn.example.com");
            var args = BrowserHost.BuildArguments(options);
            Assert.Contains("--proxy-server=" + BrowserHost.BlackHoleProxy, args);
            Assert.Contains("--proxy-bypass-list=cdn.example.com;<-loopback>", args);

            // 呼び出し元がプロキシを指定した場合は加えない(社内のプロキシが必要な環境)
            options.ChromiumArguments.Add("--proxy-server=http://proxy.example.com:8080");
            Assert.DoesNotContain(BrowserHost.BuildArguments(options), a => a.Contains(BrowserHost.BlackHoleProxy, StringComparison.Ordinal));
        }

        private static Task<ReportRenderResult> Render(string body, Action<Cshtml2PdfOptions>? configure = null) =>
            ReportRendererWaitTests.RenderAsync(ReportRendererWaitTests.Page(body), new FakeVirtualOriginHandler(), configure);
    }
}
