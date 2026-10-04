using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;

namespace Hanga.Rendering
{
    /// <summary>ページを開くための設定。</summary>
    internal sealed class ReportPageSetup
    {
        public ReportPageSetup(string virtualOrigin, string html, IVirtualOriginHandler handler)
        {
            VirtualOrigin = virtualOrigin.TrimEnd('/');
            Html = html;
            Handler = handler;
        }

        /// <summary>仮想オリジン(末尾の / なし)。</summary>
        public string VirtualOrigin { get; }

        /// <summary>帳票のHTML(<c>/__hanga/report</c> で返す)。</summary>
        public string Html { get; }

        public IVirtualOriginHandler Handler { get; }

        /// <summary>仮想オリジン以外で取得を許すホスト(要件3.4)。</summary>
        public IReadOnlyCollection<string> AllowedExternalHosts { get; set; } = Array.Empty<string>();

        /// <summary><c>/__hanga/</c> の下で返すファイル(キーは <c>/__hanga/</c> の後の名前)。</summary>
        public IReadOnlyDictionary<string, HangaResource> Resources { get; set; } = new Dictionary<string, HangaResource>();

        /// <summary>仮想オリジンへの要求 1 件の応答の大きさの上限(バイト)。</summary>
        public long MaxResponseBodyBytes { get; set; } = long.MaxValue;
    }

    /// <summary>
    /// 帳票 1 件のページ(design.md「⑤⑥」)。要求への介入で、Chromium からの要求を振り分ける。
    /// <list type="bullet">
    /// <item><c>&lt;仮想オリジン&gt;/__hanga/report</c>: 帳票の HTML。</item>
    /// <item><c>&lt;仮想オリジン&gt;/__hanga/...</c>: Hanga 自身のファイル。</item>
    /// <item><c>&lt;仮想オリジン&gt;/favicon.ico</c>: 空の応答(失敗として扱わない。要件3.7)。</item>
    /// <item>その他の仮想オリジンへの要求: <see cref="IVirtualOriginHandler"/> へ渡す(要件3.1)。300 番台・400 以上は失敗として記録する(要件3.5)。</item>
    /// <item>許可した外部ホスト: そのまま通す。それ以外の外部: 遮断して警告(要件3.4, 3.6)。</item>
    /// </list>
    /// </summary>
    internal sealed class ReportPage
    {
        internal const string ReportPath = "/__hanga/report";
        private const string ResourcePrefix = "/__hanga/";

        private static readonly HashSet<string> DroppedResponseHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Chromium 側の Cookie は使わない(元の要求の Cookie だけを使う)ため、アプリが返す Set-Cookie は渡さない
            "Set-Cookie", "Content-Type", "Content-Length", "Transfer-Encoding", "Connection", "Keep-Alive",
        };

        private readonly ReportPageSetup setup;
        private readonly ILogger logger;
        private readonly CancellationToken cancellationToken;
        private readonly ConcurrentQueue<FailedRequest> failedRequests = new ConcurrentQueue<FailedRequest>();
        private readonly ConcurrentQueue<HangaWarning> warnings = new ConcurrentQueue<HangaWarning>();
        private readonly ConcurrentDictionary<IRequest, string> pendingRequests = new ConcurrentDictionary<IRequest, string>();

        private ReportPage(IPage page, ReportPageSetup setup, ILogger logger, CancellationToken cancellationToken)
        {
            Page = page;
            this.setup = setup;
            this.logger = logger;
            this.cancellationToken = cancellationToken;
        }

        public IPage Page { get; }

        /// <summary>失敗した仮想オリジンへの要求(要件3.5)。</summary>
        public IReadOnlyList<FailedRequest> FailedRequests => failedRequests.ToList();

        /// <summary>ページで見つかった問題(外部への要求の遮断・スクリプトの例外など)。</summary>
        public IReadOnlyList<HangaWarning> Warnings => warnings.ToList();

        /// <summary>まだ完了していない要求の URL(タイムアウトの説明に使う。要件4.4)。</summary>
        public IReadOnlyList<string> PendingRequestUrls => pendingRequests.Values.ToList();

        /// <summary>ページを作り、要求への介入を有効にする。まだ移動はしない(<see cref="NavigateAsync"/>)。</summary>
        public static async Task<ReportPage> CreateAsync(RenderLease lease, ReportPageSetup setup, ILogger logger, CancellationToken cancellationToken)
        {
            IPage page = await lease.Context.NewPageAsync().ConfigureAwait(false);
            var reportPage = new ReportPage(page, setup, logger, cancellationToken);
            await page.SetRequestInterceptionAsync(true).ConfigureAwait(false);
            page.Request += reportPage.OnRequest;
            page.RequestFinished += (s, e) => reportPage.pendingRequests.TryRemove(e.Request, out _);
            page.RequestFailed += (s, e) => reportPage.pendingRequests.TryRemove(e.Request, out _);
            page.PageError += (s, e) => reportPage.AddWarning(new HangaWarning(HangaWarningKind.ScriptError, "ページの JavaScript で例外が発生しました。", e.Message));
            return reportPage;
        }

        /// <summary>
        /// 空白のページから、ページ内の JavaScript で仮想オリジンの帳票の URL へ移動し、ネットワークが静止するまで待つ(要件2.4, 4.1)。
        /// <c>Page.navigate</c>(<c>GoToAsync</c>)は使わない(PuppeteerSharp 18.1.0 は新しい Chrome で失敗する。要件2.3)。
        /// </summary>
        public async Task NavigateAsync(TimeSpan timeout)
        {
            var navigation = Page.WaitForNavigationAsync(new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.Networkidle0 },
                Timeout = (int)Math.Min(int.MaxValue, timeout.TotalMilliseconds),
            });

            // 移動は評価の後に行わせる(評価中に移動すると、評価の結果を受け取る前に実行コンテキストが破棄されるため)
            string url = setup.VirtualOrigin + ReportPath;
            await Page.EvaluateExpressionAsync($"setTimeout(function () {{ location.href = {JsString(url)}; }}, 0)").ConfigureAwait(false);
            await navigation.ConfigureAwait(false);
        }

        internal void AddWarning(HangaWarning warning) => warnings.Enqueue(warning);

        /// <summary>JavaScript の文字列リテラルにする(ページの内容を式に埋め込まないため、URL など Hanga 自身の値だけに使う)。</summary>
        internal static string JsString(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

        private async void OnRequest(object? sender, RequestEventArgs e)
        {
            // async void のイベントのため、例外を外へ出さない。要求には必ず 1 回だけ応答する
            IRequest request = e.Request;
            pendingRequests[request] = StripQuery(request.Url);
            try
            {
                await RouteAsync(request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Chromium からの要求の処理中にエラーが発生しました: {Method} {Url}", request.Method, request.Url);
                failedRequests.Enqueue(new FailedRequest(request.Method.ToString().ToUpperInvariant(), request.Url, 0));
                try
                {
                    await request.RespondAsync(new ResponseData { Status = HttpStatusCode.InternalServerError, Body = string.Empty }).ConfigureAwait(false);
                }
                catch (Exception respondEx)
                {
                    logger.LogDebug(respondEx, "要求への応答に失敗しました(ページが閉じられた可能性があります)。");
                }
            }
        }

        private async Task RouteAsync(IRequest request)
        {
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? uri))
            {
                await BlockAsync(request).ConfigureAwait(false);
                return;
            }

            if (!IsVirtualOrigin(uri))
            {
                if (IsAllowedExternalHost(uri))
                {
                    await request.ContinueAsync().ConfigureAwait(false);
                }
                else
                {
                    await BlockAsync(request).ConfigureAwait(false);
                }

                return;
            }

            string path = Uri.UnescapeDataString(uri.AbsolutePath);
            if (path == ReportPath)
            {
                await RespondAsync(request, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(setup.Html), null).ConfigureAwait(false);
                return;
            }

            if (path.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                if (setup.Resources.TryGetValue(path.Substring(ResourcePrefix.Length), out HangaResource? resource))
                {
                    await RespondAsync(request, 200, resource.ContentType, resource.Content, null).ConfigureAwait(false);
                }
                else
                {
                    failedRequests.Enqueue(new FailedRequest(Method(request), request.Url, 404));
                    await RespondAsync(request, 404, null, Array.Empty<byte>(), null).ConfigureAwait(false);
                }

                return;
            }

            if (path == "/favicon.ico")
            {
                // Chromium が自動で行う要求。アプリには渡さず、失敗として扱わない(要件3.7)
                await RespondAsync(request, 204, null, Array.Empty<byte>(), null).ConfigureAwait(false);
                return;
            }

            var virtualRequest = new VirtualRequest(
                Method(request),
                path,
                uri.Query,
                new Dictionary<string, string>(request.Headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
                request.PostData == null ? null : Encoding.UTF8.GetBytes(request.PostData.ToString() ?? string.Empty));
            VirtualResponse response = await setup.Handler.HandleAsync(virtualRequest, cancellationToken).ConfigureAwait(false);
            if (response.Body.LongLength > setup.MaxResponseBodyBytes)
            {
                failedRequests.Enqueue(new FailedRequest(virtualRequest.Method, request.Url, response.StatusCode));
                await RespondAsync(request, 502, null, Array.Empty<byte>(), null).ConfigureAwait(false);
                return;
            }

            if (response.StatusCode >= 300)
            {
                // 300 番台(ログイン画面への転送を含む)・400 以上は失敗として記録し、表示の後にエラーにする(要件3.5)
                failedRequests.Enqueue(new FailedRequest(virtualRequest.Method, request.Url, response.StatusCode));
            }

            await RespondAsync(request, response.StatusCode, response.ContentType, response.Body, response.Headers).ConfigureAwait(false);
        }

        private async Task BlockAsync(IRequest request)
        {
            AddWarning(new HangaWarning(HangaWarningKind.BlockedExternalRequest, "許可していない外部への要求を遮断しました。", StripQuery(request.Url)));
            await request.AbortAsync().ConfigureAwait(false);
        }

        private static async Task RespondAsync(IRequest request, int status, string? contentType, byte[] body, IReadOnlyDictionary<string, string>? headers)
        {
            var data = new ResponseData { Status = (HttpStatusCode)status, BodyData = body };
            if (contentType != null)
            {
                data.ContentType = contentType;
            }

            if (headers != null)
            {
                data.Headers = headers.Where(h => !DroppedResponseHeaders.Contains(h.Key)).ToDictionary(h => h.Key, h => (object)h.Value);
            }

            await request.RespondAsync(data).ConfigureAwait(false);
        }

        private bool IsVirtualOrigin(Uri uri) =>
            string.Equals(uri.GetLeftPart(UriPartial.Authority), setup.VirtualOrigin, StringComparison.OrdinalIgnoreCase);

        private bool IsAllowedExternalHost(Uri uri) =>
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && setup.AllowedExternalHosts.Any(h => string.Equals(h.Trim(), uri.Host, StringComparison.OrdinalIgnoreCase));

        private static string Method(IRequest request) => request.Method.ToString().ToUpperInvariant();

        /// <summary>警告に載せる URL からクエリ文字列を除く(秘密の値が含まれうるため。要件8.5)。</summary>
        private static string StripQuery(string url)
        {
            int q = url.IndexOfAny(new[] { '?', '#' });
            return q < 0 ? url : url.Substring(0, q);
        }
    }
}
