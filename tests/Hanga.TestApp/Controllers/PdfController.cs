using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hanga.TestApp.Controllers
{
    /// <summary>PDF 用アクション(Hanga の使い方の例を兼ねる)。</summary>
    public class PdfController : Controller
    {
        /// <summary>別のコントローラー(Home)のビューを、モデルと ViewBag を付けてブラウザで開く形で返す。</summary>
        public async Task<IActionResult> Index(bool strict = false, string? orientation = null)
        {
            ViewBag.Message = "PDF 用アクションから";
            var pdf = new Cshtml2Pdf(HttpContext, "Home", "Index", "注文123");
            pdf.Options.Strict = strict ? true : (bool?)null;
            if (orientation == "landscape")
            {
                pdf.Options.Orientation = PageOrientation.Landscape;
            }

            return await pdf.ToActionResultAsync("ホーム画面.pdf", PdfDisposition.Inline);
        }

        /// <summary>このコントローラーの ViewData を引き継ぐ形(コンストラクターにコントローラーを渡す)。ダウンロードさせる。</summary>
        public async Task<IActionResult> Report()
        {
            ViewBag.Message = "コントローラーの ViewBag";
            var pdf = new Cshtml2Pdf(this, "~/Views/Home/Index.cshtml", "帳票のモデル");
            return await pdf.ToActionResultAsync("帳票 2026年10月.pdf", PdfDisposition.Attachment);
        }

        /// <summary>ログインが必要な API から表示時に値を取得するビュー。</summary>
        [Authorize]
        public async Task<IActionResult> Order()
        {
            var pdf = new Cshtml2Pdf(HttpContext, "Home", "Order");
            return await pdf.ToActionResultAsync("注文明細.pdf");
        }

        /// <summary>ログインを要求しない PDF 用アクションから、ログインが必要な API を使うビューを PDF にする(API がログイン画面へ転送される場合のテスト用)。</summary>
        public async Task<IActionResult> OrderWithoutLogin()
        {
            return await new Cshtml2Pdf(HttpContext, "Home", "Order").ToActionResultAsync();
        }

        /// <summary>
        /// 警告の一覧を 1 行ずつのテキストで返す(テスト用)。strict を指定すると、帳票ごとに厳格な扱いを上書きする。
        /// JSON(Json(...))で返さないのは、テスト用のホスト(Microsoft.AspNetCore.TestHost 5.0.17)と新しいランタイムの System.Text.Json の組み合わせで失敗するため(docs/開発環境メモ.md)。
        /// </summary>
        public async Task<IActionResult> Warnings(string view = "Index", bool? strict = null)
        {
            var pdf = new Cshtml2Pdf(HttpContext, "Home", view);
            pdf.Options.Strict = strict;
            var document = await pdf.GenerateAsync();
            return Content(string.Join("\n", document.Warnings.Select(w => w.ToString())), "text/plain; charset=utf-8");
        }
    }
}
