using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Hanga
{
    /// <summary>
    /// PDF を返す結果(要件7.2〜7.4)。<c>Content-Disposition</c> を <c>inline</c> / <c>attachment</c> で付け、
    /// ファイル名は ASCII の代替名(<c>filename</c>)と UTF-8 の名前(<c>filename*</c>、RFC 5987)の両方を付ける。
    /// </summary>
    internal sealed class PdfActionResult : IActionResult
    {
        private readonly byte[] content;
        private readonly string? fileName;
        private readonly PdfDisposition disposition;

        public PdfActionResult(byte[] content, string? fileName, PdfDisposition disposition)
        {
            this.content = content;
            this.fileName = fileName;
            this.disposition = disposition;
        }

        /// <summary><c>Content-Disposition</c> の値を組み立てる。</summary>
        internal static string BuildContentDisposition(string? fileName, PdfDisposition disposition)
        {
            var header = new ContentDispositionHeaderValue(disposition == PdfDisposition.Attachment ? "attachment" : "inline");
            if (!string.IsNullOrEmpty(fileName))
            {
                // ASCII 以外の文字を含む名前は、filename に代替名(ASCII 以外を _ にしたもの)、filename* に UTF-8 の名前を入れる
                header.SetHttpFileName(fileName);
            }

            return header.ToString();
        }

        public Task ExecuteResultAsync(ActionContext context)
        {
            context.HttpContext.Response.Headers[HeaderNames.ContentDisposition] = BuildContentDisposition(fileName, disposition);
            return new FileContentResult(content, "application/pdf").ExecuteResultAsync(context);
        }
    }
}
