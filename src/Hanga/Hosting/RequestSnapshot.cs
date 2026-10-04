using System;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Hanga.Hosting
{
    /// <summary>
    /// 元の要求から、仮想オリジンへの要求の転送に必要な値を写し取ったもの(design.md「①」、要件3.2, 3.3, 9.3)。
    /// 元の <see cref="HttpContext"/> はスレッドセーフではないため、Chromium のイベント(別のスレッド)からは参照せず、この写しを使う。
    /// </summary>
    internal sealed class RequestSnapshot
    {
        private RequestSnapshot(string scheme, HostString host, PathString pathBase, StringValues cookie, StringValues acceptLanguage, IPAddress? remoteIpAddress, IServiceScopeFactory scopeFactory)
        {
            Scheme = scheme;
            Host = host;
            PathBase = pathBase;
            Cookie = cookie;
            AcceptLanguage = acceptLanguage;
            RemoteIpAddress = remoteIpAddress;
            ScopeFactory = scopeFactory;
        }

        /// <summary>元の要求を処理したときにアプリが認識していたスキーム。リバースプロキシの背後でも、元の要求と同じ扱いにするため。</summary>
        public string Scheme { get; }

        public HostString Host { get; }

        public PathString PathBase { get; }

        /// <summary>オペレーターの <c>Cookie</c> ヘッダー(認証 Cookie を含む)。例外・ログに出さない(要件8.5)。</summary>
        public StringValues Cookie { get; }

        public StringValues AcceptLanguage { get; }

        public IPAddress? RemoteIpAddress { get; }

        /// <summary>要求ごとのスコープを作る。ルートのプロバイダーに由来し、元の要求の終了後も使える。</summary>
        public IServiceScopeFactory ScopeFactory { get; }

        public static RequestSnapshot From(HttpContext context)
        {
            var services = context.RequestServices ?? throw new InvalidOperationException("HttpContext.RequestServices が設定されていません。");
            HttpRequest request = context.Request;
            return new RequestSnapshot(
                request.Scheme,
                request.Host,
                request.PathBase,
                request.Headers["Cookie"],
                request.Headers["Accept-Language"],
                context.Connection.RemoteIpAddress,
                services.GetRequiredService<IServiceScopeFactory>());
        }
    }
}
