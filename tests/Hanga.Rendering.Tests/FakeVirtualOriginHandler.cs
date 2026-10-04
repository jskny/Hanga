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
        private readonly Dictionary<string, Func<VirtualRequest, CancellationToken, Task<VirtualResponse>>> routes = new Dictionary<string, Func<VirtualRequest, CancellationToken, Task<VirtualResponse>>>();

        public ConcurrentQueue<VirtualRequest> Received { get; } = new ConcurrentQueue<VirtualRequest>();

        public FakeVirtualOriginHandler Text(string path, string contentType, string body, int status = 200, Dictionary<string, string>? headers = null)
        {
            routes[path] = (_, _) => Task.FromResult(new VirtualResponse(status, contentType, Encoding.UTF8.GetBytes(body), headers));
            return this;
        }

        public FakeVirtualOriginHandler On(string path, Func<VirtualRequest, VirtualResponse> respond)
        {
            routes[path] = (r, _) => Task.FromResult(respond(r));
            return this;
        }

        /// <summary>応答を遅らせる(遅いAPIの代わり)。</summary>
        public FakeVirtualOriginHandler Delayed(string path, TimeSpan delay, string contentType, string body)
        {
            routes[path] = async (_, _) =>
            {
                await Task.Delay(delay);
                return new VirtualResponse(200, contentType, Encoding.UTF8.GetBytes(body));
            };
            return this;
        }

        /// <summary>取り消しを受け取る応答(取り消されたかどうかを確かめるため)。</summary>
        public FakeVirtualOriginHandler OnCancellable(string path, Func<VirtualRequest, CancellationToken, Task<VirtualResponse>> respond)
        {
            routes[path] = respond;
            return this;
        }

        public Task<VirtualResponse> HandleAsync(VirtualRequest request, CancellationToken cancellationToken)
        {
            Received.Enqueue(request);
            return routes.TryGetValue(request.Path, out var respond)
                ? respond(request, cancellationToken)
                : Task.FromResult(new VirtualResponse(404, "text/plain", Encoding.UTF8.GetBytes("not found")));
        }
    }
}
