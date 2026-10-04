using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hanga.Hosting;
using Hanga.Templating;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Hanga
{
    /// <summary>
    /// ビューから PDF を作る簡易 API(Utsushi の <c>Excel2Pdf</c> に相当。design.md「公開API」)。
    /// <code>
    /// var pdf = new Cshtml2Pdf(this, "Order", model);
    /// pdf.Options.Orientation = PageOrientation.Landscape;
    /// return await pdf.ToActionResultAsync("注文明細.pdf", PdfDisposition.Inline);
    /// </code>
    /// スレッドセーフではない。帳票 1 件(要求 1 件)ごとに作る(要件9.1)。共有の変換器(<see cref="HangaPdfConverter"/>)は <c>AddHanga</c> で登録される。
    /// バッチでは <see cref="HangaBatch"/> から作る(batch-pdf-generation の design.md「帳票1件ごと」)。
    /// </summary>
    public sealed class Cshtml2Pdf
    {
        // Web アプリ(PDF 用アクション)から作った場合だけ設定する。バッチでは帳票 1 件ごとに GenerateAsync の中で作る
        private readonly HttpContext? httpContext;
        private readonly RequestSnapshot? snapshot;
        private readonly HangaBatch? batch;
        private readonly string controllerName;
        private readonly string viewName;
        private readonly object? model;
        private readonly ViewDataDictionary? viewData;
        private readonly HangaPdfConverter converter;

        /// <summary>コントローラーのビューから作る。コントローラー名・<c>ViewData</c>(<c>ViewBag</c>)はコントローラーのものを使う(要件1.3)。</summary>
        /// <param name="controller">PDF 用アクションのコントローラー(通常は <c>this</c>)。</param>
        /// <param name="viewName">ビュー名(例: <c>"Order"</c>)、または <c>~/Views/...cshtml</c> の形のパス。</param>
        /// <param name="model">ビューに渡すモデル。</param>
        public Cshtml2Pdf(Controller controller, string viewName, object? model = null)
            : this(
                (controller ?? throw new ArgumentNullException(nameof(controller))).HttpContext,
                (string?)controller.RouteData.Values["controller"] ?? throw new ArgumentException("コントローラー名を取得できません。", nameof(controller)),
                viewName,
                model,
                controller.ViewData)
        {
        }

        /// <summary>コントローラーの外(ミドルウェアなど)から、コントローラー名を指定して作る。</summary>
        /// <param name="httpContext">元の要求。</param>
        /// <param name="controllerName">ビューを探すときのコントローラー名(例: <c>"Home"</c> なら <c>Views/Home/</c>)。</param>
        /// <param name="viewName">ビュー名、または <c>~/Views/...cshtml</c> の形のパス。</param>
        /// <param name="model">ビューに渡すモデル。</param>
        public Cshtml2Pdf(HttpContext httpContext, string controllerName, string viewName, object? model = null)
            : this(httpContext, controllerName, viewName, model, null)
        {
        }

        private Cshtml2Pdf(HttpContext httpContext, string controllerName, string viewName, object? model, ViewDataDictionary? viewData)
        {
            this.httpContext = httpContext ?? throw new ArgumentNullException(nameof(httpContext));
            this.controllerName = RequireName(controllerName, nameof(controllerName), "コントローラー名");
            this.viewName = RequireName(viewName, nameof(viewName), "ビュー名");
            this.model = model;
            this.viewData = viewData;
            if (httpContext.Items.ContainsKey(PipelineForwarder.ForwardedRequestMarker))
            {
                // 帳票のページが PDF 用アクションを読み込んでいる(iframe など)。入れ子で生成すると同時実行の枠を食い合うため、すぐにエラーにする
                throw new HangaConfigurationException("Hanga が帳票の表示のために転送した要求の中で、PDF を生成しようとしました。帳票のビューから PDF 用アクションを読み込まないでください。");
            }

            converter = httpContext.RequestServices?.GetService<HangaPdfConverter>()
                ?? throw new HangaConfigurationException("Hanga が登録されていません。Startup.ConfigureServices で services.AddHanga(...) を呼んでください。");

            // 元の要求の値を作成時に写し取る(要件9.3)
            snapshot = RequestSnapshot.From(httpContext);
        }

        /// <summary>
        /// バッチ(Web アプリとは別の実行ファイル)で、<see cref="HangaBatch"/> のビューから作る(batch-pdf-generation の要件1.5, 2.3)。
        /// ビューに渡す値はすべてモデルで渡す(バッチにはオペレーターがいないため、Cookie・ログインユーザー・セッションは使えない)。
        /// </summary>
        /// <param name="batch">バッチ用の変換器。</param>
        /// <param name="controllerName">ビューを探すときのコントローラー名(例: <c>"Invoice"</c> なら <c>Views/Invoice/</c>)。</param>
        /// <param name="viewName">ビュー名、または <c>~/Views/...cshtml</c> の形のパス。</param>
        /// <param name="model">ビューに渡すモデル。</param>
        public Cshtml2Pdf(HangaBatch batch, string controllerName, string viewName, object? model = null)
        {
            this.batch = batch ?? throw new ArgumentNullException(nameof(batch));
            batch.ThrowIfDisposed();
            this.controllerName = RequireName(controllerName, nameof(controllerName), "コントローラー名");
            this.viewName = RequireName(viewName, nameof(viewName), "ビュー名");
            this.model = model;
            converter = batch.Converter;
        }

        /// <summary>帳票 1 件の体裁の設定。</summary>
        public Cshtml2PdfOptions Options { get; } = new Cshtml2PdfOptions();

        /// <summary>PDF を生成し、PDF と警告の一覧を返す。</summary>
        /// <param name="cancellationToken">取り消し。指定しなければ、元の要求の取り消し(オペレーターがブラウザを閉じた場合など)を使う(要件9.6)。</param>
        public Task<HangaPdfDocument> GenerateAsync(CancellationToken cancellationToken = default)
        {
            if (batch != null)
            {
                return GenerateInBatchAsync(batch, cancellationToken);
            }

            var view = new ViewRenderRequest(httpContext!, controllerName, viewName, model)
            {
                ViewData = viewData,
                RouteData = httpContext!.GetRouteData(),
            };
            return converter.GenerateAsync(view, snapshot!, Options, Effective(cancellationToken));
        }

        /// <summary>PDF のバイト列を返す(要件7.1)。</summary>
        public async Task<byte[]> ToBytesAsync(CancellationToken cancellationToken = default) =>
            (await GenerateAsync(cancellationToken).ConfigureAwait(false)).Content;

        /// <summary>PDF をストリームに書き込む(要件7.1)。</summary>
        public async Task WriteToAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            byte[] pdf = await ToBytesAsync(cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(pdf, 0, pdf.Length, Effective(cancellationToken)).ConfigureAwait(false);
        }

        /// <summary>
        /// PDF をファイルに保存する(要件7.1)。同じフォルダの一時ファイルに書き終えてから、保存先の名前に変える(batch-pdf-generation の要件4.2)。
        /// 生成・書き込みの途中で失敗した場合や取り消された場合は、保存先にファイルを作らず(既にあるファイルは変えず)、一時ファイルも残さない。
        /// </summary>
        /// <param name="path">保存先のファイル。既にあれば置き換える。フォルダは呼び出し元が用意する。</param>
        /// <param name="cancellationToken">取り消し。</param>
        public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("保存先のファイルを指定してください。", nameof(path));
            }

            byte[] pdf = await ToBytesAsync(cancellationToken).ConfigureAwait(false);
            string fullPath = Path.GetFullPath(path);
            // 一時ファイルの名前は短くする(深いフォルダで Windows のパスの長さの上限を超えにくくするため)
            string temporary = fullPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                // 同じ名前のファイル・リンクがあれば上書きせずに失敗させる(他の利用者も書き込めるフォルダに保存する場合に備える)
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await stream.WriteAsync(pdf, 0, pdf.Length, Effective(cancellationToken)).ConfigureAwait(false);
                }

                File.Move(temporary, fullPath, overwrite: true);
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }
        }

        /// <summary>
        /// PDF 用アクションからそのまま返せる結果を作る(要件7.2〜7.4)。
        /// </summary>
        /// <param name="fileName">ファイル名(日本語可)。null ならファイル名を付けない。</param>
        /// <param name="disposition">ブラウザで開くか、ダウンロードさせるか。</param>
        /// <param name="cancellationToken">取り消し。指定しなければ元の要求の取り消しを使う。</param>
        public async Task<IActionResult> ToActionResultAsync(string? fileName = null, PdfDisposition disposition = PdfDisposition.Inline, CancellationToken cancellationToken = default)
        {
            HangaPdfDocument document = await GenerateAsync(cancellationToken).ConfigureAwait(false);
            return new PdfActionResult(document.Content, fileName, disposition);
        }

        private static string RequireName(string value, string paramName, string label) =>
            string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(label + "を指定してください。", paramName) : value;

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // 消せなくても元の例外を優先する
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>バッチでは、帳票 1 件ごとにスコープと HttpContext を作り、生成が終わったら破棄する(batch-pdf-generation の design.md「帳票1件用の HttpContext」)。</summary>
        private async Task<HangaPdfDocument> GenerateInBatchAsync(HangaBatch owner, CancellationToken cancellationToken)
        {
            await using BatchRequest request = owner.CreateRequest();
            var view = new ViewRenderRequest(request.HttpContext, controllerName, viewName, model);
            return await converter.GenerateAsync(view, RequestSnapshot.From(request.HttpContext), Options, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>取り消しの指定が無ければ、元の要求の取り消しを使う(バッチでは元の要求が無いため、指定されたものだけを使う)。</summary>
        private CancellationToken Effective(CancellationToken cancellationToken) =>
            cancellationToken.CanBeCanceled || httpContext == null ? cancellationToken : httpContext.RequestAborted;
    }
}
