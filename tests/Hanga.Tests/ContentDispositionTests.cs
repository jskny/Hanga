using Xunit;

namespace Hanga.Tests
{
    /// <summary>Content-Disposition の組み立て(要件7.3, 7.4)。</summary>
    public class ContentDispositionTests
    {
        [Fact]
        public void ブラウザで開く形と日本語のファイル名()
        {
            string value = PdfActionResult.BuildContentDisposition("注文明細.pdf", PdfDisposition.Inline);
            Assert.StartsWith("inline;", value);
            Assert.Contains("filename*=UTF-8''%E6%B3%A8%E6%96%87%E6%98%8E%E7%B4%B0.pdf", value); // 「注文明細」の UTF-8(RFC 5987)
            Assert.Contains("filename=", value); // ASCII の代替名
        }

        [Fact]
        public void ダウンロードさせる形()
        {
            Assert.StartsWith("attachment;", PdfActionResult.BuildContentDisposition("report.pdf", PdfDisposition.Attachment));
        }

        [Fact]
        public void ファイル名を付けない場合()
        {
            Assert.Equal("inline", PdfActionResult.BuildContentDisposition(null, PdfDisposition.Inline));
        }
    }
}
