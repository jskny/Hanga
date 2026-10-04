using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hanga.Sample.Controllers
{
    public sealed class InvoiceLine
    {
        public string Name { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal Amount => Quantity * UnitPrice;
    }

    public sealed class InvoiceModel
    {
        public string InvoiceNo { get; set; } = string.Empty;
        public int CustomerId { get; set; }
        public List<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();
    }

    /// <summary>サンプルの帳票。画面(Invoice など)と、それを PDF にするアクション(InvoicePdf など)。</summary>
    [Authorize]
    public class ReportsController : Controller
    {
        /// <summary>検証ツールが対象にする帳票の名前。</summary>
        public static readonly string[] Names = { "Invoice", "Wide", "Gaiji" };

        public IActionResult Invoice() => View(CreateInvoice());

        public IActionResult Wide() => View();

        public IActionResult Gaiji() => View();

        /// <summary>請求書を PDF にしてブラウザで開く。</summary>
        public async Task<IActionResult> InvoicePdf()
        {
            var pdf = new Cshtml2Pdf(this, "Invoice", CreateInvoice());
            pdf.Options.PageNumbers = true;
            return await pdf.ToActionResultAsync("請求書.pdf", PdfDisposition.Inline);
        }

        /// <summary>幅の広い表を PDF にする(既定で用紙の幅に収める)。横向き。</summary>
        public async Task<IActionResult> WidePdf()
        {
            var pdf = new Cshtml2Pdf(this, "Wide");
            pdf.Options.Orientation = PageOrientation.Landscape;
            return await pdf.ToActionResultAsync("幅の広い表.pdf", PdfDisposition.Attachment);
        }

        /// <summary>外字・異体字を含む帳票を PDF にする(外字用フォントを設定している場合に正しく出る)。</summary>
        public async Task<IActionResult> GaijiPdf()
        {
            return await new Cshtml2Pdf(this, "Gaiji").ToActionResultAsync("外字.pdf");
        }

        private static InvoiceModel CreateInvoice() => new InvoiceModel
        {
            InvoiceNo = "INV-2026-0001",
            CustomerId = 42,
            Lines = Enumerable.Range(1, 45).Select(i => new InvoiceLine { Name = "商品" + i, Quantity = i % 7 + 1, UnitPrice = 1234.5m * i }).ToList(),
        };
    }
}
