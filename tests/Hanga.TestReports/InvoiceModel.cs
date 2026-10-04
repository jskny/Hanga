using System.Collections.Generic;
using System.Linq;

namespace Hanga.TestReports
{
    /// <summary>請求書の帳票のモデル。バッチでは、帳票に載せる値をすべてモデルで渡す。</summary>
    public sealed class InvoiceModel
    {
        public string CustomerName { get; set; } = string.Empty;

        public List<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();

        public decimal Total => Lines.Sum(l => l.Amount);
    }

    public sealed class InvoiceLine
    {
        public InvoiceLine(string name, decimal amount)
        {
            Name = name;
            Amount = amount;
        }

        public string Name { get; }

        public decimal Amount { get; }
    }

    /// <summary>帳票 1 件ごとのスコープのサービス。帳票ごとにスコープが分かれ、破棄されることを確かめる(batch-pdf-generation の要件2.4)。</summary>
    public interface IReportScope
    {
        int Id { get; }
    }

    /// <summary>ビューが <c>@inject</c> で使うサービス。バッチ側で登録する(batch-pdf-generation の要件2.5)。</summary>
    public interface ICompanyInfo
    {
        string CompanyName { get; }
    }
}
