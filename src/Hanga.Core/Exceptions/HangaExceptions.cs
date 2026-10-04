using System;
using System.Collections.Generic;
using System.Linq;

namespace Hanga
{
    /// <summary>設定の誤り(Chromium の場所、外字用フォントが無い、範囲外の値など)。</summary>
    public sealed class HangaConfigurationException : HangaException
    {
        public HangaConfigurationException(string message, Exception? innerException = null)
            : base(message, HangaStage.Configuration, innerException)
        {
        }
    }

    /// <summary>指定されたビューが見つからない(要件1.4)。</summary>
    public sealed class HangaViewNotFoundException : HangaException
    {
        public HangaViewNotFoundException(string viewName, IEnumerable<string> searchedLocations)
            : base(BuildMessage(viewName, searchedLocations), HangaStage.ViewRendering)
        {
            ViewName = viewName;
            SearchedLocations = searchedLocations.ToList();
        }

        public string ViewName { get; }

        /// <summary>ビューを探した場所。</summary>
        public IReadOnlyList<string> SearchedLocations { get; }

        private static string BuildMessage(string viewName, IEnumerable<string> searchedLocations) =>
            $"ビュー '{viewName}' が見つかりません。探した場所: {string.Join(", ", searchedLocations)}";
    }

    /// <summary>ビューの描画中に例外が発生した(要件1.5)。元の例外を内部例外に持つ。</summary>
    public sealed class HangaViewRenderingException : HangaException
    {
        public HangaViewRenderingException(string viewName, Exception innerException)
            : base($"ビュー '{viewName}' の描画中にエラーが発生しました: {innerException.Message}", HangaStage.ViewRendering, innerException)
        {
            ViewName = viewName;
        }

        public string ViewName { get; }
    }

    /// <summary>Chromium の起動、または Chromium への命令に失敗した(要件8.3)。</summary>
    public sealed class HangaBrowserException : HangaException
    {
        public HangaBrowserException(string message, string executablePath, string? browserVersion, HangaStage stage, Exception? innerException = null)
            : base(BuildMessage(message, executablePath, browserVersion), stage, innerException)
        {
            ExecutablePath = executablePath;
            BrowserVersion = browserVersion;
        }

        /// <summary>Chromium の実行ファイルの場所。</summary>
        public string ExecutablePath { get; }

        /// <summary>Chromium の版。取得できなかった場合は null。</summary>
        public string? BrowserVersion { get; }

        private static string BuildMessage(string message, string executablePath, string? browserVersion) =>
            $"{message}(Chromium: {executablePath}、版: {browserVersion ?? "不明"})";
    }

    /// <summary>仮想オリジンへの要求(静的ファイル・API)の応答が失敗(400以上)またはリダイレクト(300番台)だった(要件3.5)。</summary>
    public sealed class HangaResourceRequestException : HangaException
    {
        public HangaResourceRequestException(IEnumerable<FailedRequest> failedRequests)
            : this(failedRequests.ToList())
        {
        }

        private HangaResourceRequestException(List<FailedRequest> failedRequests)
            : base(BuildMessage(failedRequests), HangaStage.ResourceRequest)
        {
            FailedRequests = failedRequests;
        }

        /// <summary>失敗した要求。</summary>
        public IReadOnlyList<FailedRequest> FailedRequests { get; }

        private static string BuildMessage(List<FailedRequest> failed) =>
            "ページが読み込んだリソースの取得に失敗したため、PDF を出力しませんでした: "
            + string.Join(", ", failed.Select(f => $"{f.Method} {f.Url} → {f.StatusCode}"));
    }

    /// <summary>失敗した要求(URL・メソッド・状態コード)。</summary>
    public sealed class FailedRequest
    {
        public FailedRequest(string method, string url, int statusCode)
        {
            Method = method;
            Url = url;
            StatusCode = statusCode;
        }

        public string Method { get; }

        public string Url { get; }

        /// <summary>状態コード。応答を得られなかった場合は 0。</summary>
        public int StatusCode { get; }
    }

    /// <summary>表示の完了を上限時間までに待ちきれなかった(要件4.4)。</summary>
    public sealed class HangaTimeoutException : HangaException
    {
        public HangaTimeoutException(TimeSpan timeout, string waitingFor, IEnumerable<string> pendingRequestUrls, Exception? innerException = null)
            : this(timeout, waitingFor, pendingRequestUrls.ToList(), innerException)
        {
        }

        private HangaTimeoutException(TimeSpan timeout, string waitingFor, List<string> pending, Exception? innerException)
            : base(BuildMessage(timeout, waitingFor, pending), HangaStage.Waiting, innerException)
        {
            Timeout = timeout;
            WaitingFor = waitingFor;
            PendingRequestUrls = pending;
        }

        public TimeSpan Timeout { get; }

        /// <summary>待っていた条件。</summary>
        public string WaitingFor { get; }

        /// <summary>上限時間の時点で完了していなかった要求の URL。</summary>
        public IReadOnlyList<string> PendingRequestUrls { get; }

        private static string BuildMessage(TimeSpan timeout, string waitingFor, List<string> pending) =>
            $"{timeout.TotalSeconds:0.#}秒以内に表示が完了しませんでした(待っていた条件: {waitingFor})。"
            + (pending.Count > 0 ? " 完了していない要求: " + string.Join(", ", pending) : string.Empty);
    }

    /// <summary>厳格な扱い(要件8.7)で、警告の対象をエラーにした。</summary>
    public sealed class HangaStrictModeException : HangaException
    {
        public HangaStrictModeException(IEnumerable<HangaWarning> warnings)
            : this(warnings.ToList())
        {
        }

        private HangaStrictModeException(List<HangaWarning> warnings)
            : base("厳格な扱いのため、次の問題をエラーとしました: " + string.Join(" / ", warnings.Select(w => w.Message)), StageOf(warnings))
        {
            Warnings = warnings;
        }

        public IReadOnlyList<HangaWarning> Warnings { get; }

        private static HangaStage StageOf(List<HangaWarning> warnings) =>
            warnings.Count > 0 && warnings[0].Kind == HangaWarningKind.BlockedExternalRequest ? HangaStage.ResourceRequest : HangaStage.PageLoad;
    }
}
