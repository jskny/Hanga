using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuppeteerSharp;

namespace Hanga.Rendering
{
    /// <summary>帳票 1 件の描画の入力。</summary>
    internal sealed class ReportRenderInput
    {
        public ReportRenderInput(string html, Cshtml2PdfOptions options, IVirtualOriginHandler handler)
        {
            Html = html;
            Options = options;
            Handler = handler;
        }

        /// <summary>ビューを HTML にしたもの。</summary>
        public string Html { get; }

        public Cshtml2PdfOptions Options { get; }

        public IVirtualOriginHandler Handler { get; }
    }

    /// <summary>帳票 1 件の描画の結果。</summary>
    internal sealed class ReportRenderResult
    {
        public ReportRenderResult(byte[] pdf, IReadOnlyList<HangaWarning> warnings, string browserVersion, TimeSpan elapsed)
        {
            Pdf = pdf;
            Warnings = warnings;
            BrowserVersion = browserVersion;
            Elapsed = elapsed;
        }

        public byte[] Pdf { get; }

        public IReadOnlyList<HangaWarning> Warnings { get; }

        public string BrowserVersion { get; }

        public TimeSpan Elapsed { get; }
    }

    /// <summary>
    /// 帳票 1 件を、ページの表示から PDF まで描画する(design.md「③〜⑩」)。スレッドセーフ(状態は帳票ごとに作る)。
    /// 待機の上限時間は、帳票全体の締め切りとして管理する(要件4.3)。
    /// </summary>
    internal sealed class ReportRenderer
    {
        private readonly BrowserHost host;
        private readonly HangaOptions global;
        private readonly GlyphSupport glyphs;
        private readonly ILogger logger;

        public ReportRenderer(BrowserHost host, HangaOptions global, GlyphSupport glyphs, ILogger? logger = null)
        {
            this.host = host;
            this.global = global;
            this.glyphs = glyphs;
            this.logger = logger ?? NullLogger.Instance;
        }

        /// <summary>
        /// 帳票を PDF にする。
        /// </summary>
        /// <exception cref="HangaResourceRequestException">仮想オリジンへの要求が失敗した(設定によらずエラー。要件3.5, 8.4)。</exception>
        /// <exception cref="HangaTimeoutException">上限時間までに表示が完了しなかった(設定によらずエラー。要件4.4, 8.4)。</exception>
        /// <exception cref="HangaStrictModeException">厳格な扱いで、警告の対象が見つかった(要件8.7)。</exception>
        public async Task<ReportRenderResult> RenderAsync(ReportRenderInput input, CancellationToken cancellationToken = default)
        {
            Cshtml2PdfOptions options = input.Options;
            options.Validate();
            TimeSpan timeout = options.EffectiveTimeout(global);
            bool strict = options.EffectiveStrict(global);
            var stopwatch = Stopwatch.StartNew();

            await using RenderLease lease = await host.AcquireAsync(cancellationToken).ConfigureAwait(false);
            var deadline = new Deadline(timeout, cancellationToken);

            var setup = new ReportPageSetup(global.NormalizedVirtualOrigin, input.Html, input.Handler)
            {
                AllowedExternalHosts = global.AllowedExternalHosts,
                MaxResponseBodyBytes = global.MaxResponseBodyBytes,
                Resources = glyphs.Resources,
            };
            ReportPage page = await ReportPage.CreateAsync(lease, setup, logger, cancellationToken).ConfigureAwait(false);

            await WaitForReadyAsync(page, options, deadline).ConfigureAwait(false);

            if (page.FailedRequests.Count > 0)
            {
                // 必要な値が欠けた PDF を返さないため、設定によらずエラーにする(要件3.5, 8.4)
                throw new HangaResourceRequestException(page.FailedRequests);
            }

            // 外字・異体字・字形の無い文字(要件6)。API から取得した値が描画された後に行う
            await deadline.RunAsync(glyphs.ApplyAsync(page.Page), "外字用フォントの適用", page).ConfigureAwait(false);
            HangaWarning? missingGlyphs = await deadline.RunAsync(glyphs.FindMissingGlyphsAsync(page.Page), "字形の確認", page).ConfigureAwait(false);
            if (missingGlyphs != null)
            {
                page.AddWarning(missingGlyphs);
            }

            ThrowIfStrict(page.Warnings, strict);

            byte[] pdf = await deadline.RunAsync(PdfPrinter.PrintAsync(page.Page, options), "PDF の出力", page).ConfigureAwait(false);

            var warnings = page.Warnings;
            foreach (var warning in warnings)
            {
                logger.LogWarning("帳票の生成で問題が見つかりました: {Warning}", warning.ToString());
            }

            return new ReportRenderResult(pdf, warnings, lease.BrowserVersion, stopwatch.Elapsed);
        }

        /// <summary>
        /// 表示の完了を待つ(要件4.1〜4.4): ① ネットワークの静止、② 完了条件の式(指定時)、③ Web フォントの読み込み。
        /// </summary>
        private static async Task WaitForReadyAsync(ReportPage page, Cshtml2PdfOptions options, Deadline deadline)
        {
            await deadline.RunAsync(page.NavigateAsync(), "ページの読み込みとネットワークの静止", page).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(options.ReadyExpression))
            {
                // 式は呼び出し元(アプリの開発者)が書いたもの。ページの内容(利用者の入力)は埋め込まない
                string function = "() => !!(" + options.ReadyExpression + ")";
                await deadline.RunAsync(
                    page.Page.WaitForFunctionAsync(function, new WaitForFunctionOptions { Timeout = 0, PollingInterval = 100 }),
                    "完了条件 " + options.ReadyExpression,
                    page).ConfigureAwait(false);
            }

            await deadline.RunAsync(page.Page.EvaluateFunctionAsync<bool>("() => document.fonts.ready.then(() => true)"), "Web フォントの読み込み", page).ConfigureAwait(false);
        }

        private static void ThrowIfStrict(IReadOnlyList<HangaWarning> warnings, bool strict)
        {
            if (strict && warnings.Count > 0)
            {
                throw new HangaStrictModeException(warnings);
            }
        }

        /// <summary>帳票全体の締め切り。各段階の待機に、残り時間を割り当てる。</summary>
        private sealed class Deadline
        {
            private readonly TimeSpan timeout;
            private readonly Stopwatch stopwatch = Stopwatch.StartNew();
            private readonly CancellationToken cancellationToken;

            public Deadline(TimeSpan timeout, CancellationToken cancellationToken)
            {
                this.timeout = timeout;
                this.cancellationToken = cancellationToken;
            }

            public async Task RunAsync(Task task, string waitingFor, ReportPage page)
            {
                await RunAsync(task.ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }, TaskScheduler.Default), waitingFor, page).ConfigureAwait(false);
            }

            /// <summary>残り時間のうちに <paramref name="task"/> が終わるのを待つ。終わらなければ <see cref="HangaTimeoutException"/>。</summary>
            public async Task<T> RunAsync<T>(Task<T> task, string waitingFor, ReportPage page)
            {
                TimeSpan remaining = timeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    Observe(task);
                    throw new HangaTimeoutException(timeout, waitingFor, page.PendingRequestUrls);
                }

                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task delay = Task.Delay(remaining, delayCts.Token);
                Task finished = await Task.WhenAny(task, delay).ConfigureAwait(false);
                if (finished == task)
                {
                    delayCts.Cancel();
                    return await task.ConfigureAwait(false);
                }

                Observe(task);
                cancellationToken.ThrowIfCancellationRequested();
                throw new HangaTimeoutException(timeout, waitingFor, page.PendingRequestUrls);
            }

            /// <summary>待つのをやめたタスクの例外を観測済みにする(ページを閉じると失敗するため)。</summary>
            private static void Observe(Task task) =>
                task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }
}
