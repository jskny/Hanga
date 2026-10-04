using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuppeteerSharp;

namespace Hanga.Rendering
{
    /// <summary>
    /// 共有の Chromium のプロセスを管理する(design.md「③④」)。スレッドセーフ。
    /// <list type="bullet">
    /// <item>最初の利用時に、排他制御のうえで起動する(要件10.2)。起動の直後に版を記録する(要件11.3)。</item>
    /// <item>プロセスが終了していたら、排他制御のうえで 1 回だけ起動し直す(要件9.5)。</item>
    /// <item>同時に処理する帳票の数を制限し(要件9.4)、帳票 1 件ごとに独立したブラウザコンテキストを作る(要件9.2)。</item>
    /// </list>
    /// </summary>
    internal sealed class BrowserHost : IAsyncDisposable
    {
        private readonly HangaOptions options;
        private readonly ILogger logger;
        private readonly SemaphoreSlim launchLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim renderSlots;
        private IBrowser? browser;
        private string? userDataDir;
        private bool disposed;

        public BrowserHost(HangaOptions options, ILogger<BrowserHost>? logger = null)
        {
            this.options = options;
            this.logger = (ILogger?)logger ?? NullLogger.Instance;
            renderSlots = new SemaphoreSlim(options.MaxConcurrentRenders, options.MaxConcurrentRenders);
        }

        /// <summary>起動中の Chromium の版。まだ起動していなければ null。</summary>
        public string? BrowserVersion { get; private set; }

        /// <summary>起動した回数(起動し直しを含む)。テストと記録に使う。</summary>
        public int LaunchCount { get; private set; }

        /// <summary>起動中の Chromium(テストでプロセスを止めるために使う)。</summary>
        internal IBrowser? CurrentBrowser => browser;

        /// <summary>Chromium を起動しておく(既に起動していれば何もしない)。アプリの起動時の事前起動(要件10.2)に使う。</summary>
        public async Task EnsureLaunchedAsync(CancellationToken cancellationToken = default)
        {
            await GetBrowserAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 同時実行数の枠を取り、帳票 1 件用のブラウザコンテキストを作る。
        /// 戻り値を破棄すると、コンテキストを閉じて枠を返す(例外・取り消しの場合も、呼び出し側が using で必ず破棄する)。
        /// </summary>
        public async Task<RenderLease> AcquireAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await renderSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                IBrowserContext context = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
                return new RenderLease(context, BrowserVersion ?? string.Empty, renderSlots, logger);
            }
            catch
            {
                renderSlots.Release();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            await launchLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await CloseBrowserAsync().ConfigureAwait(false);
            }
            finally
            {
                launchLock.Release();
            }
        }

        private async Task<IBrowserContext> CreateContextAsync(CancellationToken cancellationToken)
        {
            IBrowser current = await GetBrowserAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await current.CreateBrowserContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (!current.IsConnected && !(ex is OperationCanceledException))
            {
                // コンテキストを作る間にプロセスが終了した。次の GetBrowserAsync で 1 回だけ起動し直す(要件9.5)
                logger.LogWarning(ex, "Chromium のプロセスが終了していたため、起動し直します。");
                IBrowser relaunched = await GetBrowserAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await relaunched.CreateBrowserContextAsync().ConfigureAwait(false);
                }
                catch (Exception retryEx) when (!(retryEx is OperationCanceledException))
                {
                    throw new HangaBrowserException("Chromium を起動し直しましたが、ブラウザコンテキストを作れませんでした。", options.ChromiumExecutablePath, BrowserVersion, HangaStage.BrowserLaunch, retryEx);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) && !(ex is HangaException))
            {
                throw new HangaBrowserException("ブラウザコンテキストを作れませんでした。", options.ChromiumExecutablePath, BrowserVersion, HangaStage.BrowserLaunch, ex);
            }
        }

        /// <summary>起動中の Chromium を返す。起動していない・プロセスが終了している場合は、排他制御のうえで起動する。</summary>
        private async Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken)
        {
            IBrowser? current = browser;
            if (current != null && current.IsConnected)
            {
                return current;
            }

            await launchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();

                // 排他制御を待つ間に、別のスレッドが起動し直していればそれを使う(起動し直しは 1 回に限る)
                if (browser != null && browser.IsConnected)
                {
                    return browser;
                }

                if (browser != null)
                {
                    logger.LogWarning("Chromium のプロセスが終了していたため、起動し直します(版: {Version})。", BrowserVersion);
                    await CloseBrowserAsync().ConfigureAwait(false);
                }

                browser = await LaunchAsync().ConfigureAwait(false);
                return browser;
            }
            finally
            {
                launchLock.Release();
            }
        }

        private async Task<IBrowser> LaunchAsync()
        {
            string path = options.ChromiumExecutablePath;
            if (!File.Exists(path))
            {
                throw new HangaBrowserException("Chromium の実行ファイルが見つかりません。", path, null, HangaStage.BrowserLaunch);
            }

            // プロセスごとの一時ユーザーデータフォルダ。終了時に削除する
            userDataDir = Path.Combine(Path.GetTempPath(), "hanga-chromium-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(userDataDir);
            try
            {
                var launchOptions = new LaunchOptions
                {
                    Headless = true,
                    ExecutablePath = path,
                    UserDataDir = userDataDir,
                    Args = options.ChromiumArguments.ToArray(),
                };
                IBrowser launched = await Puppeteer.LaunchAsync(launchOptions).ConfigureAwait(false);
                BrowserVersion = await launched.GetVersionAsync().ConfigureAwait(false);
                LaunchCount++;
                logger.LogInformation("Chromium を起動しました(版: {Version}、場所: {Path})。", BrowserVersion, path);
                return launched;
            }
            catch (Exception ex)
            {
                DeleteUserDataDir();
                throw new HangaBrowserException("Chromium を起動できませんでした。", path, null, HangaStage.BrowserLaunch, ex);
            }
        }

        private async Task CloseBrowserAsync()
        {
            IBrowser? closing = browser;
            browser = null;
            if (closing != null)
            {
                try
                {
                    await closing.CloseAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Chromium の終了中にエラーが発生しました(既に終了していた可能性があります)。");
                }

                closing.Dispose();
            }

            DeleteUserDataDir();
        }

        private void DeleteUserDataDir()
        {
            string? dir = userDataDir;
            userDataDir = null;
            if (dir == null)
            {
                return;
            }

            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                // Chromium の子プロセスがまだファイルを掴んでいる場合など。次回以降の動作には影響しない
                logger.LogDebug(ex, "Chromium の一時ユーザーデータフォルダを削除できませんでした: {Dir}", dir);
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(BrowserHost));
            }
        }
    }

    /// <summary>帳票 1 件用のブラウザコンテキストと、同時実行数の枠。破棄するとコンテキストを閉じて枠を返す。</summary>
    internal sealed class RenderLease : IAsyncDisposable
    {
        private readonly SemaphoreSlim slots;
        private readonly ILogger logger;
        private int disposed;

        public RenderLease(IBrowserContext context, string browserVersion, SemaphoreSlim slots, ILogger logger)
        {
            Context = context;
            BrowserVersion = browserVersion;
            this.slots = slots;
            this.logger = logger;
        }

        public IBrowserContext Context { get; }

        public string BrowserVersion { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 1)
            {
                return;
            }

            try
            {
                await Context.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "ブラウザコンテキストを閉じる際にエラーが発生しました(Chromium が終了していた可能性があります)。");
            }
            finally
            {
                slots.Release();
            }
        }
    }
}
