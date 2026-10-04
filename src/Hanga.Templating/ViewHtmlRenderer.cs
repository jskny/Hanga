using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hanga.Templating
{
    /// <summary>
    /// 呼び出し元アプリの ASP.NET Core MVC のビュー描画の仕組み(<see cref="IRazorViewEngine"/>)で、ビューを HTML にする(要件1、design.md「②」)。
    /// 画面を表示するときと同じ規則でビューを探すため、<c>_ViewStart</c>・レイアウト・<c>~/</c>・タグヘルパーが画面と同じ結果になる。
    /// </summary>
    internal sealed class ViewHtmlRenderer
    {
        /// <summary>ビューを HTML にする。</summary>
        /// <exception cref="HangaViewNotFoundException">ビューが見つからない(要件1.4)。</exception>
        /// <exception cref="HangaViewRenderingException">ビューの描画中に例外が発生した(要件1.5)。</exception>
        public async Task<string> RenderAsync(ViewRenderRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var services = request.HttpContext.RequestServices
                ?? throw new InvalidOperationException("HttpContext.RequestServices が設定されていません。");

            var actionContext = CreateActionContext(request);
            IView view = FindView(services.GetRequiredService<IRazorViewEngine>(), actionContext, request.ViewName);

            try
            {
                var viewData = CreateViewData(services, request);
                var tempData = services.GetRequiredService<ITempDataDictionaryFactory>().GetTempData(request.HttpContext);
                var htmlHelperOptions = services.GetRequiredService<IOptions<MvcViewOptions>>().Value.HtmlHelperOptions;
                using var writer = new StringWriter();
                var viewContext = new ViewContext(actionContext, view, viewData, tempData, writer, htmlHelperOptions);
                await view.RenderAsync(viewContext).ConfigureAwait(false);
                return writer.ToString();
            }
            catch (Exception ex) when (!(ex is HangaException) && !(ex is OperationCanceledException))
            {
                throw new HangaViewRenderingException(request.ViewName, ex);
            }
            finally
            {
                (view as IDisposable)?.Dispose();
            }
        }

        /// <summary>
        /// ビューを探すための <see cref="ActionContext"/> を作る。
        /// ビューの探索はコントローラー名を <see cref="ActionDescriptor.RouteValues"/> から優先して読むため、
        /// PDF 用アクションの <see cref="ActionDescriptor"/> を流用せず、指定のコントローラー名を入れた新しいものを作る(要件1.3)。
        /// </summary>
        private static ActionContext CreateActionContext(ViewRenderRequest request)
        {
            var routeData = request.RouteData != null ? new RouteData(request.RouteData) : new RouteData();
            routeData.Values["controller"] = request.ControllerName;
            routeData.Values["action"] = request.ViewName;

            var descriptor = new ActionDescriptor();
            descriptor.RouteValues["controller"] = request.ControllerName;
            descriptor.RouteValues["action"] = request.ViewName;
            if (routeData.Values.TryGetValue("area", out object? area) && area != null)
            {
                descriptor.RouteValues["area"] = area.ToString() ?? string.Empty;
            }

            return new ActionContext(request.HttpContext, routeData, descriptor);
        }

        /// <summary>MVC の <c>ViewResult</c> と同じく、パスとしての指定(<c>GetView</c>)を試してから、名前としての探索(<c>FindView</c>)を行う。</summary>
        private static IView FindView(IRazorViewEngine engine, ActionContext actionContext, string viewName)
        {
            ViewEngineResult byPath = engine.GetView(executingFilePath: null, viewPath: viewName, isMainPage: true);
            if (byPath.Success)
            {
                return byPath.View;
            }

            ViewEngineResult byName = engine.FindView(actionContext, viewName, isMainPage: true);
            if (byName.Success)
            {
                return byName.View;
            }

            throw new HangaViewNotFoundException(viewName, byPath.SearchedLocations.Concat(byName.SearchedLocations).Distinct());
        }

        private static ViewDataDictionary CreateViewData(IServiceProvider services, ViewRenderRequest request)
        {
            // コントローラーの ViewData(ViewBag を含む)は写しを作って引き継ぎ、元を書き換えない
            var viewData = request.ViewData != null
                ? new ViewDataDictionary(request.ViewData)
                : new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary());
            viewData.Model = request.Model;
            return viewData;
        }
    }
}
