using System.Threading;
using System.Threading.Tasks;
using Hanga.Rendering;
using Microsoft.Extensions.Hosting;

namespace Hanga.Hosting
{
    /// <summary>
    /// アプリの起動・停止に合わせて Chromium を起動・終了する(要件10.2)。
    /// <see cref="HangaOptions.LaunchOnStartup"/> なら起動時に Chromium を起動する(起動できなければアプリの起動を失敗させ、問題を早く知らせる)。
    /// Chromium の終了は、Web サーバーが処理中の要求を終えた後(<see cref="IHostApplicationLifetime.ApplicationStopped"/>)に行う。
    /// この常駐サービスの停止は Web サーバーの停止より先に呼ばれるため、ここで終了すると処理中の PDF の要求が失敗するため。
    /// </summary>
    internal sealed class HangaHostedService : IHostedService
    {
        private readonly BrowserHost host;
        private readonly HangaOptions options;
        private readonly IHostApplicationLifetime lifetime;

        public HangaHostedService(BrowserHost host, HangaOptions options, IHostApplicationLifetime lifetime)
        {
            this.host = host;
            this.options = options;
            this.lifetime = lifetime;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            lifetime.ApplicationStopped.Register(() => host.Dispose());
            return options.LaunchOnStartup ? host.EnsureLaunchedAsync(cancellationToken) : Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
