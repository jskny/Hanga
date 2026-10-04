using System.Threading;
using System.Threading.Tasks;
using Hanga.Rendering;
using Microsoft.Extensions.Hosting;

namespace Hanga.Hosting
{
    /// <summary>
    /// アプリの起動・停止に合わせて Chromium を起動・終了する(要件10.2)。
    /// <see cref="HangaOptions.LaunchOnStartup"/> なら起動時に Chromium を起動する(起動できなければアプリの起動を失敗させ、問題を早く知らせる)。
    /// </summary>
    internal sealed class HangaHostedService : IHostedService
    {
        private readonly BrowserHost host;
        private readonly HangaOptions options;

        public HangaHostedService(BrowserHost host, HangaOptions options)
        {
            this.host = host;
            this.options = options;
        }

        public Task StartAsync(CancellationToken cancellationToken) =>
            options.LaunchOnStartup ? host.EnsureLaunchedAsync(cancellationToken) : Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken) => await host.DisposeAsync().ConfigureAwait(false);
    }
}
