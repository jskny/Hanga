using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hanga.Rendering;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hanga.Hosting
{
    /// <summary>
    /// 仮想オリジンへの要求を、呼び出し元アプリのパイプラインにプロセス内で渡す(design.md「⑤⑥」、要件3.1〜3.3, 3.8)。
    /// ネットワーク(ソケット)を使わないため、ポート・HTTPS 証明書・リバースプロキシの構成に左右されない。
    /// </summary>
    internal sealed class PipelineForwarder : IVirtualOriginHandler
    {
        /// <summary>Chromium が付けた値を使わず、元の要求の値を使うヘッダー。</summary>
        private static readonly HashSet<string> ReplacedRequestHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Cookie", "Host", "Accept-Language", "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Content-Length",
        };

        /// <summary>Hanga が転送した要求の印(<see cref="HttpContext.Items"/> のキー)。転送した要求の中で PDF を生成しようとした場合に気づくため。</summary>
        internal static readonly object ForwardedRequestMarker = new object();

        private readonly RequestSnapshot snapshot;
        private readonly RequestDelegate pipeline;
        private readonly long maxResponseBodyBytes;
        private readonly ILogger logger;

        public PipelineForwarder(RequestSnapshot snapshot, RequestDelegate pipeline, long maxResponseBodyBytes, ILogger logger)
        {
            this.snapshot = snapshot;
            this.pipeline = pipeline;
            this.maxResponseBodyBytes = maxResponseBodyBytes;
            this.logger = logger;
        }

        public async Task<VirtualResponse> HandleAsync(VirtualRequest request, CancellationToken cancellationToken)
        {
            using IServiceScope scope = snapshot.ScopeFactory.CreateScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            HttpRequest target = context.Request;
            target.Method = request.Method;
            target.Scheme = snapshot.Scheme;
            target.Host = snapshot.Host;
            target.PathBase = snapshot.PathBase;
            target.Path = new PathString(request.Path);
            target.QueryString = new QueryString(string.IsNullOrEmpty(request.QueryString) ? null : request.QueryString);
            target.Protocol = "HTTP/1.1";
            foreach (var header in request.Headers.Where(h => !ReplacedRequestHeaders.Contains(h.Key)))
            {
                target.Headers[header.Key] = header.Value;
            }

            // オペレーターの認証 Cookie を引き継ぐ(Chromium 側の Cookie は使わない。要件3.2)
            if (snapshot.Cookie.Count > 0)
            {
                target.Headers["Cookie"] = snapshot.Cookie;
            }

            if (snapshot.AcceptLanguage.Count > 0)
            {
                target.Headers["Accept-Language"] = snapshot.AcceptLanguage;
            }

            if (request.Body != null)
            {
                target.Body = new MemoryStream(request.Body, writable: false);
                target.ContentLength = request.Body.Length;
            }

            context.Connection.RemoteIpAddress = snapshot.RemoteIpAddress;
            context.Items[ForwardedRequestMarker] = true;
            context.RequestAborted = cancellationToken;

            var body = new BoundedMemoryStream(maxResponseBodyBytes);
            context.Response.Body = body;
            try
            {
                await pipeline(context).ConfigureAwait(false);
            }
            catch (ResponseTooLargeException)
            {
                logger.LogWarning("アプリの応答が大きすぎるため、失敗として扱います(上限 {Limit} バイト): {Method} {Path}", maxResponseBodyBytes, request.Method, request.Path);
                return new VirtualResponse(502, null, Array.Empty<byte>());
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                logger.LogWarning(ex, "アプリのパイプラインで例外が発生しました: {Method} {Path}", request.Method, request.Path);
                return new VirtualResponse(500, null, Array.Empty<byte>());
            }

            var headers = context.Response.Headers
                .Where(h => !string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            return new VirtualResponse(context.Response.StatusCode, context.Response.ContentType, body.ToArray(), headers);
        }

        /// <summary>上限を超えて書き込まれたら <see cref="ResponseTooLargeException"/> を投げるメモリ上のストリーム(メモリを使い尽くさないための安全弁)。</summary>
        private sealed class BoundedMemoryStream : MemoryStream
        {
            private readonly long limit;

            public BoundedMemoryStream(long limit)
            {
                this.limit = limit;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                Check(count);
                base.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                Check(buffer.Length);
                base.Write(buffer);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Check(count);
                return base.WriteAsync(buffer, offset, count, cancellationToken);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Check(buffer.Length);
                return base.WriteAsync(buffer, cancellationToken);
            }

            public override void WriteByte(byte value)
            {
                Check(1);
                base.WriteByte(value);
            }

            private void Check(int count)
            {
                if (Length + count > limit)
                {
                    throw new ResponseTooLargeException();
                }
            }
        }

        private sealed class ResponseTooLargeException : IOException
        {
        }
    }
}
