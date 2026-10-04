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

        /// <summary>帳票 1 件の、仮想オリジンへの要求の応答の合計の上限(バイト)。</summary>
        public long MaxTotalResponseBytes { get; set; } = long.MaxValue;
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

        /// <summary>種類ごとの警告の数(上限を超えた分は記録せず、数だけ数える)。</summary>
        private readonly ConcurrentDictionary<HangaWarningKind, int> warningCounts = new ConcurrentDictionary<HangaWarningKind, int>();
        private readonly ConcurrentDictionary<IRequest, string> pendingRequests = new ConcurrentDictionary<IRequest, string>();

        /// <summary>許可した外部ホストへ通した要求(失敗を記録するため)。</summary>
        private readonly ConcurrentDictionary<IRequest, bool> continuedExternalRequests = new ConcurrentDictionary<IRequest, bool>();
        private long totalResponseBytes;

        /// <summary>帳票の URL への最初の移動を始めたか(それ以外のページ全体の移動は止める。要件2.7)。</summary>
        private int reportNavigationStarted;

        /// <summary>移動を止めたことがあるか(表示の完了の待ち方を切り替えるため)。</summary>
        private readonly TaskCompletionSource<bool> navigationBlocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

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

        /// <summary>種類ごとに記録する警告の上限(ダイアログを繰り返し出すページなどで、メモリとログを使い尽くさないため)。</summary>
        internal const int MaxWarningsPerKind = 20;

        /// <summary>警告の詳細の長さの上限(文字数)。</summary>
        internal const int MaxWarningDetailLength = 200;

        /// <summary>ページで見つかった問題(外部への要求の遮断・スクリプトの例外など)。上限を超えた種類は「ほか N 件」の警告を加える。</summary>
        public IReadOnlyList<HangaWarning> Warnings
        {
            get
            {
                var list = warnings.ToList();
                foreach (var count in warningCounts.Where(c => c.Value > MaxWarningsPerKind).OrderBy(c => c.Key))
                {
                    list.Add(new HangaWarning(count.Key, $"同じ種類の警告が、ほかに {count.Value - MaxWarningsPerKind} 件ありました(記録を省略しました)。"));
                }

                return list;
            }
        }

        /// <summary>まだ完了していない要求の URL(タイムアウトの説明に使う。要件4.4)。</summary>
        public IReadOnlyList<string> PendingRequestUrls => pendingRequests.Values.ToList();

        /// <summary>ページを作り、要求への介入を有効にする。まだ移動はしない(<see cref="NavigateAsync"/>)。</summary>
        public static async Task<ReportPage> CreateAsync(RenderLease lease, ReportPageSetup setup, ILogger logger, CancellationToken cancellationToken)
        {
            IPage page = await lease.Context.NewPageAsync().ConfigureAwait(false);
            var reportPage = new ReportPage(page, setup, logger, cancellationToken);
            await page.SetRequestInterceptionAsync(true).ConfigureAwait(false);
            page.Request += reportPage.OnRequest;
            page.RequestFinished += (s, e) => reportPage.OnExternalFinished(e.Request, failed: false);
            page.RequestFailed += (s, e) => reportPage.OnExternalFinished(e.Request, failed: true);
            page.Response += (s, e) => reportPage.OnExternalResponse(e.Response);
            page.PageError += (s, e) => reportPage.AddWarning(new HangaWarning(HangaWarningKind.ScriptError, "ページの JavaScript で例外が発生しました。", e.Message));
            page.Dialog += reportPage.OnDialog;
            page.Popup += reportPage.OnPopup;
            return reportPage;
        }

        /// <summary>
        /// 空白のページから、ページ内の JavaScript で仮想オリジンの帳票の URL へ移動し、ネットワークが静止するまで待つ(要件2.4, 4.1)。
        /// <c>Page.navigate</c>(<c>GoToAsync</c>)は使わない(PuppeteerSharp 18.1.0 は新しい Chrome で失敗する。要件2.3)。
        /// 上限時間は呼び出し側(<see cref="ReportRenderer"/>)が帳票全体の締め切りとして管理するため、ここでは時間を区切らない。
        /// </summary>
        public async Task NavigateAsync()
        {
            var navigation = Page.WaitForNavigationAsync(new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.Networkidle0 },
                Timeout = 0,
            });

            // 移動は評価の後に行わせる(評価中に移動すると、評価の結果を受け取る前に実行コンテキストが破棄されるため)
            string url = setup.VirtualOrigin + ReportPath;
            try
            {
                await Page.EvaluateExpressionAsync($"setTimeout(function () {{ location.href = {JsString(url)}; }}, 0)").ConfigureAwait(false);
            }
            catch
            {
                // 移動の待機を放置しない(ページを閉じると失敗するため、例外を観測済みにする)
                _ = navigation.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                throw;
            }

            // 移動を止めた(204 で応えた)後は、Chromium がネットワークの静止を通知しないことがある(負荷が高いときに起きた)。
            // その場合は、ページの読み込みの完了と、Hanga が扱う要求が一定時間無いことを自分で確かめて、待機を終える
            Task fallback = WaitForQuietAfterBlockedNavigationAsync();
            if (await Task.WhenAny(navigation, fallback).ConfigureAwait(false) == navigation)
            {
                await navigation.ConfigureAwait(false);
                return;
            }

            _ = navigation.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            await fallback.ConfigureAwait(false);
        }

        /// <summary>移動を止めた後に、ページの読み込みが完了し、要求が 500 ミリ秒の間無いことを待つ(ネットワークの静止と同じ基準)。</summary>
        private async Task WaitForQuietAfterBlockedNavigationAsync()
        {
            await navigationBlocked.Task.ConfigureAwait(false);
            var quiet = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool complete;
                try
                {
                    complete = IsOnReportPage && await Page.EvaluateExpressionAsync<bool>("document.readyState === 'complete'").ConfigureAwait(false);
                }
                catch (PuppeteerException)
                {
                    complete = false;
                }

                if (!complete || pendingRequests.Count > 0)
                {
                    quiet.Restart();
                }
                else if (quiet.ElapsedMilliseconds >= 500)
                {
                    return;
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        internal void AddWarning(HangaWarning warning)
        {
            if (warningCounts.AddOrUpdate(warning.Kind, 1, (_, n) => n + 1) > MaxWarningsPerKind)
            {
                return;
            }

            string? detail = warning.Detail;
            if (detail != null && detail.Length > MaxWarningDetailLength)
            {
                detail = detail.Substring(0, MaxWarningDetailLength) + "…";
                warning = new HangaWarning(warning.Kind, warning.Message, detail);
            }

            warnings.Enqueue(warning);
        }

        /// <summary>今のページが帳票のページか(about:blank など、要求を出さない移動で離れていないか。要件2.7)。</summary>
        public bool IsOnReportPage =>
            string.Equals(StripQuery(Page.Url), setup.VirtualOrigin + ReportPath, StringComparison.OrdinalIgnoreCase);

        /// <summary>JavaScript の文字列リテラルにする(ページの内容を式に埋め込まないため、URL など Hanga 自身の値だけに使う)。</summary>
        internal static string JsString(string value)
        {
            var builder = new StringBuilder("'");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '\'': builder.Append("\\'"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\u2028': builder.Append("\\u2028"); break;
                    case '\u2029': builder.Append("\\u2029"); break;
                    case '<': builder.Append("\\u003C"); break;
                    default: builder.Append(c); break;
                }
            }

            return builder.Append('\'').ToString();
        }

        /// <summary>許可した外部ホストへ通した要求が失敗した(名前解決の失敗など)。失敗として記録する(要件3.5)。</summary>
        private void OnExternalFinished(IRequest request, bool failed)
        {
            pendingRequests.TryRemove(request, out _);
            if (continuedExternalRequests.TryRemove(request, out _) && failed)
            {
                failedRequests.Enqueue(new FailedRequest(Method(request), StripQuery(request.Url), 0));
            }
        }

        /// <summary>許可した外部ホストからの応答が失敗(400 以上)だった。失敗として記録する(要件3.5)。</summary>
        private void OnExternalResponse(IResponse response)
        {
            if (response.Request != null && continuedExternalRequests.ContainsKey(response.Request) && (int)response.Status >= 400)
            {
                continuedExternalRequests.TryRemove(response.Request, out _);
                failedRequests.Enqueue(new FailedRequest(Method(response.Request), StripQuery(response.Request.Url), (int)response.Status));
            }
        }

        /// <summary>
        /// ダイアログは、閉じないとページの JavaScript が止まり、タイムアウトになる。閉じて続け、警告として記録する(要件4.6)。
        /// alert は「OK」で閉じる。confirm・prompt・離脱の確認は「キャンセル」で閉じる。誰も答えていない確認を「OK」として扱うと、
        /// 「削除しますか」などの確認の後の処理が、オペレーターの権限で実行されてしまうため(セキュリティレビューの指摘)。
        /// 文言は氏名などを含みうるため詳細に入れる。
        /// </summary>
        private async void OnDialog(object? sender, DialogEventArgs e)
        {
            bool isAlert = e.Dialog.DialogType == DialogType.Alert;
            AddWarning(new HangaWarning(
                HangaWarningKind.Dialog,
                $"ページの JavaScript がダイアログ({e.Dialog.DialogType})を出したため、「{(isAlert ? "OK" : "キャンセル")}」で閉じて続けました。",
                e.Dialog.Message));
            try
            {
                if (isAlert)
                {
                    await e.Dialog.Accept().ConfigureAwait(false);
                }
                else
                {
                    await e.Dialog.Dismiss().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "ダイアログを閉じられませんでした(ページが閉じられた可能性があります)。");
            }
        }

        /// <summary>
        /// 別のウィンドウ(window.open・target=_blank)は、要求への介入が効かないため、すぐに閉じて警告にする(要件2.7)。
        /// 閉じる前に出た要求は、行き止まりのプロキシ(BrowserHost の起動引数)で外へ出ない。
        /// </summary>
        private async void OnPopup(object? sender, PopupEventArgs e)
        {
            AddWarning(new HangaWarning(HangaWarningKind.BlockedNavigation, "ページが別のウィンドウを開こうとしたため、閉じました。", StripQuery(e.PopupPage.Url)));
            try
            {
                await e.PopupPage.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "別のウィンドウを閉じられませんでした。");
            }
        }

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
                logger.LogWarning(ex, "Chromium からの要求の処理中にエラーが発生しました: {Method} {Url}", request.Method, StripQuery(request.Url));
                failedRequests.Enqueue(new FailedRequest(request.Method.ToString().ToUpperInvariant(), StripQuery(request.Url), 0));
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
            if (request.IsNavigationRequest && ShouldBlockNavigation(request))
            {
                // 帳票を開いた後のページ全体の移動(location.href の変更・フォームの送信・再読み込み)と、内側のフレームのフォームの送信は止め、
                // 元のページを PDF にする。止めないと、移動先の内容の PDF を黙って返したり、送信がアプリに届いたりする(要件2.7)。
                // 要求を中断(abort)すると Chromium がエラーページへ移るため、ブラウザが元のページに留まる 204(No Content)で応える
                AddWarning(new HangaWarning(HangaWarningKind.BlockedNavigation, "帳票を開いた後に、ページが別の URL へ移動しようとしたため、止めました。", StripQuery(request.Url)));
                await RespondAsync(request, 204, null, Array.Empty<byte>(), null).ConfigureAwait(false);

                // 止めた移動の要求は、完了の通知が来ないことがあるため、完了していない要求から外す(待機が終わらなくなるため)
                pendingRequests.TryRemove(request, out _);
                navigationBlocked.TrySetResult(true);
                return;
            }

            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? uri))
            {
                await BlockAsync(request).ConfigureAwait(false);
                return;
            }

            if (uri.Scheme == "data" || uri.Scheme == "blob")
            {
                // ページの中で完結するデータ(インラインの画像など)。外部への通信ではないため通す
                await request.ContinueAsync().ConfigureAwait(false);
                return;
            }

            if (!IsVirtualOrigin(uri))
            {
                if (IsAllowedExternalHost(uri))
                {
                    continuedExternalRequests[request] = true;
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
                    failedRequests.Enqueue(new FailedRequest(Method(request), StripQuery(request.Url), 404));
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
            long total = Interlocked.Add(ref totalResponseBytes, response.Body.LongLength);
            if (response.Body.LongLength > setup.MaxResponseBodyBytes || total > setup.MaxTotalResponseBytes)
            {
                failedRequests.Enqueue(new FailedRequest(virtualRequest.Method, StripQuery(request.Url), response.StatusCode));
                await RespondAsync(request, 502, null, Array.Empty<byte>(), null).ConfigureAwait(false);
                return;
            }

            if (response.StatusCode >= 300)
            {
                // 300 番台(ログイン画面への転送を含む)・400 以上は失敗として記録し、表示の後にエラーにする(要件3.5)
                failedRequests.Enqueue(new FailedRequest(virtualRequest.Method, StripQuery(request.Url), response.StatusCode));
            }

            await RespondAsync(request, response.StatusCode, response.ContentType, response.Body, response.Headers).ConfigureAwait(false);
        }

        /// <summary>
        /// 止める移動か。ページ全体の移動は、帳票の URL への最初の 1 回だけを通す(フレームが分からない場合はページ全体として扱い、止める側に倒す)。
        /// 内側のフレームは、GET 以外(フォームの送信)を止める。
        /// </summary>
        private bool ShouldBlockNavigation(IRequest request)
        {
            bool mainFrame = request.Frame == null || request.Frame == Page.MainFrame;
            if (!mainFrame)
            {
                return Method(request) != "GET";
            }

            bool isReport = Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? uri)
                && IsVirtualOrigin(uri)
                && Uri.UnescapeDataString(uri.AbsolutePath) == ReportPath;
            return !(isReport && Interlocked.CompareExchange(ref reportNavigationStarted, 1, 0) == 0);
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

        /// <summary>例外・警告・ログに載せる URL からクエリ文字列を除く(秘密の値が含まれうるため。要件8.5)。</summary>
        private static string StripQuery(string url)
        {
            int q = url.IndexOfAny(new[] { '?', '#' });
            return q < 0 ? url : url.Substring(0, q);
        }
    }
}
