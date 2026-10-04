using System;
using System.Threading;
using System.Threading.Tasks;
using Hanga.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hanga
{
    /// <summary>
    /// バッチ用の変換器(batch-pdf-generation の design.md「公開API」)。Web アプリとは別の実行ファイル(バッチ)の中で、ビューから PDF を作る。
    /// アプリケーションサーバーにはアクセスせず、ビューの描画の仕組み(Web サーバーを持たない ASP.NET Core のホスト)と Chromium をバッチの中に用意する。
    /// <code>
    /// await using var batch = await HangaBatch.StartAsync(options, b =&gt; b.ViewAssemblies.Add(typeof(InvoiceModel).Assembly));
    /// var pdf = new Cshtml2Pdf(batch, "Invoice", "Invoice", model);
    /// await pdf.SaveAsync(path);
    /// </code>
    /// スレッドセーフで、バッチ全体で 1 つを共有する(要件1.5)。帳票 1 件ごとの入口は <see cref="Cshtml2Pdf"/>。
    /// </summary>
    public sealed class HangaBatch : IAsyncDisposable, IDisposable
    {
        private readonly IHost host;
        private readonly HangaOptions options;
        private int disposed;

        private HangaBatch(IHost host, HangaOptions options, HangaPdfConverter converter)
        {
            this.host = host;
            this.options = options;
            Converter = converter;
        }

        /// <summary>起動した Chromium の版。</summary>
        public string? ChromiumVersion => Converter.ChromiumVersion;

        internal HangaPdfConverter Converter { get; }

        /// <summary>ホストのサービス(テストから Hanga の部品を確かめるため)。</summary>
        internal IServiceProvider Services => host.Services;

        /// <summary>
        /// バッチ用の変換器を起動する。ホストを作って起動し、Chromium を起動する(要件1.1〜1.3)。
        /// 設定の誤り・Chromium の起動の失敗は、帳票の生成を始める前に、ここで例外にする。
        /// </summary>
        /// <param name="options">アプリ全体の設定(Web アプリと同じ型。設定ファイルから読む場合は <c>configuration.GetSection("Hanga").Bind(options)</c>)。</param>
        /// <param name="configure">バッチ固有の設定(ビューのアセンブリ・静的ファイルのフォルダなど)。</param>
        /// <param name="cancellationToken">起動の取り消し。</param>
        /// <exception cref="HangaConfigurationException">設定に誤りがある。</exception>
        /// <exception cref="HangaBrowserException">Chromium を起動できない。</exception>
        public static async Task<HangaBatch> StartAsync(HangaOptions options, Action<HangaBatchOptions>? configure = null, CancellationToken cancellationToken = default)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var batchOptions = new HangaBatchOptions();
            configure?.Invoke(batchOptions);
            options.Validate();
            batchOptions.Validate();

            IHost host = BatchHostBuilder.Build(options, batchOptions);
            try
            {
                await host.StartAsync(cancellationToken).ConfigureAwait(false);
                var converter = host.Services.GetRequiredService<HangaPdfConverter>();
                await converter.WarmUpAsync(cancellationToken).ConfigureAwait(false);
                return new HangaBatch(host, options, converter);
            }
            catch
            {
                // 起動に失敗したら、起動しかけた Chromium も含めて後始末してから例外を返す
                await StopAndDisposeAsync(host).ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>ホストを停止し、Chromium を終了する(要件1.4)。生成中の帳票がすべて終わってから呼ぶ。</summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                await StopAndDisposeAsync(host).ConfigureAwait(false);
            }
        }

        /// <summary>ホストを停止し、Chromium を終了する(要件1.4)。</summary>
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        /// <summary>
        /// 帳票 1 件用の依存性注入のスコープと <see cref="HttpContext"/> を作る(design.md「帳票1件用の HttpContext」、要件2.4, 3.4)。
        /// スキームとホストは仮想オリジンの値にする。Cookie などオペレーターの情報は付けない。
        /// </summary>
        internal BatchRequest CreateRequest()
        {
            ThrowIfDisposed();
            IServiceScope scope = host.Services.CreateScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            var origin = new Uri(options.NormalizedVirtualOrigin);
            context.Request.Scheme = origin.Scheme;
            context.Request.Host = HostString.FromUriComponent(origin);
            context.Request.Method = HttpMethods.Get;

            // タグヘルパーの URL の生成がエンドポイントルーティングの仕組みを使うようにする(検証レポート「6.4」)
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "Hanga batch"));
            return new BatchRequest(scope, context);
        }

        internal void ThrowIfDisposed()
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(HangaBatch), "HangaBatch は終了しています。");
            }
        }

        private static async Task StopAndDisposeAsync(IHost host)
        {
            try
            {
                await host.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                // Chromium の終了は、ホストの停止(ApplicationStopped)で行われる(HangaHostedService)。破棄で依存性注入のシングルトンも破棄する
                if (host is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    host.Dispose();
                }
            }
        }
    }

    /// <summary>帳票 1 件用のスコープと <see cref="HttpContext"/>。生成が終わったら破棄する。</summary>
    internal sealed class BatchRequest : IDisposable
    {
        private readonly IServiceScope scope;

        public BatchRequest(IServiceScope scope, HttpContext httpContext)
        {
            this.scope = scope;
            HttpContext = httpContext;
        }

        public HttpContext HttpContext { get; }

        public void Dispose() => scope.Dispose();
    }
}
