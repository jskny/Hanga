using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hanga.Rendering.Tests
{
    /// <summary>テスト用の振り分け先。パスごとに決めた応答を返し、受け取った要求を記録する。</summary>
    internal sealed class FakeVirtualOriginHandler : IVirtualOriginHandler
    {
        private readonly Dictionary<string, Func<VirtualRequest, VirtualResponse>> routes = new Dictionary<string, Func<VirtualRequest, VirtualResponse>>();

        public ConcurrentQueue<VirtualRequest> Received { get; } = new ConcurrentQueue<VirtualRequest>();

        public FakeVirtualOriginHandler Text(string path, string contentType, string body, int status = 200, Dictionary<string, string>? headers = null)
        {
            routes[path] = _ => new VirtualResponse(status, contentType, Encoding.UTF8.GetBytes(body), headers);
            return this;
        }

        public FakeVirtualOriginHandler On(string path, Func<VirtualRequest, VirtualResponse> respond)
        {
            routes[path] = respond;
            return this;
        }

        public Task<VirtualResponse> HandleAsync(VirtualRequest request, CancellationToken cancellationToken)
        {
            Received.Enqueue(request);
            return Task.FromResult(routes.TryGetValue(request.Path, out var respond)
                ? respond(request)
                : new VirtualResponse(404, "text/plain", Encoding.UTF8.GetBytes("not found")));
        }
    }
}
