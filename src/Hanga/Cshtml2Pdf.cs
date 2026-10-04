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
    /// </summary>
    public sealed class Cshtml2Pdf
    {
        private readonly HttpContext httpContext;
        private readonly string controllerName;
        private readonly string viewName;
        private readonly object? model;
        private readonly ViewDataDictionary? viewData;
        private readonly RequestSnapshot snapshot;
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
            this.controllerName = string.IsNullOrWhiteSpace(controllerName) ? throw new ArgumentException("コントローラー名を指定してください。", nameof(controllerName)) : controllerName;
            this.viewName = string.IsNullOrWhiteSpace(viewName) ? throw new ArgumentException("ビュー名を指定してください。", nameof(viewName)) : viewName;
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

        /// <summary>帳票 1 件の体裁の設定。</summary>
        public Cshtml2PdfOptions Options { get; } = new Cshtml2PdfOptions();

        /// <summary>PDF を生成し、PDF と警告の一覧を返す。</summary>
        /// <param name="cancellationToken">取り消し。指定しなければ、元の要求の取り消し(オペレーターがブラウザを閉じた場合など)を使う(要件9.6)。</param>
        public Task<HangaPdfDocument> GenerateAsync(CancellationToken cancellationToken = default)
        {
            var view = new ViewRenderRequest(httpContext, controllerName, viewName, model)
            {
                ViewData = viewData,
                RouteData = httpContext.GetRouteData(),
            };
            return converter.GenerateAsync(view, snapshot, Options, Effective(cancellationToken));
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

        /// <summary>PDF をファイルに保存する(要件7.1)。生成に失敗した場合はファイルを作らない。</summary>
        public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
        {
            byte[] pdf = await ToBytesAsync(cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(path, pdf, Effective(cancellationToken)).ConfigureAwait(false);
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

        private CancellationToken Effective(CancellationToken cancellationToken) =>
            cancellationToken.CanBeCanceled ? cancellationToken : httpContext.RequestAborted;
    }
}
