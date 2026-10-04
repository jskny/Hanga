using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hanga.Rendering
{
    /// <summary>
    /// 仮想オリジンへの要求(静的ファイル・API)を受け取って応答を返す(要件3.1)。
    /// PuppeteerSharp・ASP.NET Core の型を含めない(要件2.6)。実装は呼び出し元アプリのパイプラインへの転送(Hanga.PipelineForwarder)。
    /// </summary>
    internal interface IVirtualOriginHandler
    {
        Task<VirtualResponse> HandleAsync(VirtualRequest request, CancellationToken cancellationToken);
    }

    /// <summary>Chromium が仮想オリジンに出した要求。</summary>
    internal sealed class VirtualRequest
    {
        public VirtualRequest(string method, string path, string queryString, IReadOnlyDictionary<string, string> headers, byte[]? body)
        {
            Method = method;
            Path = path;
            QueryString = queryString;
            Headers = headers;
            Body = body;
        }

        /// <summary>HTTP のメソッド(大文字)。</summary>
        public string Method { get; }

        /// <summary>パス(デコード済み。先頭は /)。</summary>
        public string Path { get; }

        /// <summary>クエリ文字列(先頭の ? を含む。無ければ空)。</summary>
        public string QueryString { get; }

        /// <summary>Chromium が付けたヘッダー(<c>Cookie</c>・<c>Host</c> は転送側で使わない)。</summary>
        public IReadOnlyDictionary<string, string> Headers { get; }

        public byte[]? Body { get; }
    }

    /// <summary>仮想オリジンへの要求に対する応答。</summary>
    internal sealed class VirtualResponse
    {
        public VirtualResponse(int statusCode, string? contentType, byte[] body, IReadOnlyDictionary<string, string>? headers = null)
        {
            StatusCode = statusCode;
            ContentType = contentType;
            Body = body;
            Headers = headers ?? new Dictionary<string, string>();
        }

        public int StatusCode { get; }

        public string? ContentType { get; }

        public byte[] Body { get; }

        /// <summary>Chromium に返すヘッダー(<c>Content-Type</c> 以外)。</summary>
        public IReadOnlyDictionary<string, string> Headers { get; }
    }

    /// <summary>Hanga 自身が <c>/__hanga/</c> で返すファイル(判定用フォント・外字用フォントのファイル)。</summary>
    internal sealed class HangaResource
    {
        public HangaResource(string contentType, byte[] content)
        {
            ContentType = contentType;
            Content = content;
        }

        public string ContentType { get; }

        public byte[] Content { get; }
    }
}
