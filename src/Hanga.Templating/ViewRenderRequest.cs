using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;

namespace Hanga.Templating
{
    /// <summary>
    /// ビューを HTML にするために必要な情報(要件1)。
    /// HTTP の要求の情報(<see cref="HttpContext"/>)は引数として受け取り、元の要求が無い場合(バッチ。要件1.6)は呼び出し側が用意する。
    /// </summary>
    internal sealed class ViewRenderRequest
    {
        public ViewRenderRequest(HttpContext httpContext, string controllerName, string viewName, object? model)
        {
            HttpContext = httpContext;
            ControllerName = controllerName;
            ViewName = viewName;
            Model = model;
        }

        /// <summary>ビューの描画に使う HTTP の要求の情報(<c>RequestServices</c> を含む)。</summary>
        public HttpContext HttpContext { get; }

        /// <summary>ビューを探すときのコントローラー名(要件1.3)。</summary>
        public string ControllerName { get; }

        /// <summary>ビュー名。<c>~/Views/...cshtml</c> の形のパスも指定できる。</summary>
        public string ViewName { get; }

        public object? Model { get; }

        /// <summary>引き継ぐ <c>ViewData</c>(コントローラーの <c>ViewData</c>・<c>ViewBag</c>)。null なら空から作る。</summary>
        public ViewDataDictionary? ViewData { get; set; }

        /// <summary>元の要求のルーティング情報(area など)。null なら空から作る。</summary>
        public RouteData? RouteData { get; set; }
    }
}
