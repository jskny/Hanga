using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Hanga.Hosting;
using Hanga.Rendering;
using Hanga.Templating;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hanga
{
    /// <summary>
    /// 共有の変換器(design.md「公開API」)。スレッドセーフで、アプリ全体で 1 つを共有する(要件9.1)。<c>AddHanga</c> で登録される。
    /// 帳票 1 件ごとの入口は <see cref="Cshtml2Pdf"/>。
    /// </summary>
    public sealed class HangaPdfConverter
    {
        private readonly HangaOptions options;
        private readonly ViewHtmlRenderer viewRenderer;
        private readonly ReportRenderer reportRenderer;
        private readonly BrowserHost browserHost;
        private readonly PipelineHolder pipeline;
        private readonly ILogger logger;

        internal HangaPdfConverter(HangaOptions options, ViewHtmlRenderer viewRenderer, ReportRenderer reportRenderer, BrowserHost browserHost, PipelineHolder pipeline, ILogger<HangaPdfConverter>? logger = null)
        {
            this.options = options;
            this.viewRenderer = viewRenderer;
            this.reportRenderer = reportRenderer;
            this.browserHost = browserHost;
            this.pipeline = pipeline;
            this.logger = (ILogger?)logger ?? NullLogger.Instance;
        }

        /// <summary>起動中の Chromium の版。まだ起動していなければ null。</summary>
        public string? ChromiumVersion => browserHost.BrowserVersion;

        /// <summary>Chromium をあらかじめ起動しておく(最初の PDF の待ち時間を短くするため。要件10.2)。</summary>
        public Task WarmUpAsync(CancellationToken cancellationToken = default) => browserHost.EnsureLaunchedAsync(cancellationToken);

        internal HangaOptions Options => options;

        internal async Task<HangaPdfDocument> GenerateAsync(ViewRenderRequest view, RequestSnapshot snapshot, Cshtml2PdfOptions pdfOptions, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            pdfOptions.Validate();

            // ② ビューの HTML 化は、元の要求のスレッド(PDF 用アクションの中)で行う
            string html = await viewRenderer.RenderAsync(view, cancellationToken).ConfigureAwait(false);

            // ③〜⑩ ページの表示から PDF まで。仮想オリジンへの要求は写し取った値でパイプラインに渡す
            var forwarder = new PipelineForwarder(snapshot, pipeline.GetRequired(), options.MaxResponseBodyBytes, logger);
            ReportRenderResult result = await reportRenderer.RenderAsync(new ReportRenderInput(html, pdfOptions, forwarder), cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "PDF を生成しました(ビュー: {Controller}/{View}、{Bytes} バイト、警告 {WarningCount} 件、{Elapsed} ミリ秒)。",
                view.ControllerName, view.ViewName, result.Pdf.Length, result.Warnings.Count, (long)stopwatch.Elapsed.TotalMilliseconds);
            return new HangaPdfDocument(result.Pdf, result.Warnings, result.BrowserVersion, stopwatch.Elapsed);
        }
    }
}
