using System;
using System.Collections.Generic;
using System.Linq;
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
    internal sealed class BrowserHost : IAsyncDisposable, IDisposable
    {
        private readonly HangaOptions options;
        private readonly ILogger logger;
        private readonly SemaphoreSlim launchLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim renderSlots;
        private readonly Func<IBrowser, Task>? afterLaunch;
        private IBrowser? browser;
        private string? userDataDir;
        private bool disposed;

        /// <param name="options">アプリ全体の設定。</param>
        /// <param name="logger">記録先。</param>
        /// <param name="afterLaunch">起動の直後に行う確認(外字用フォントの有無など。要件6.6)。例外を投げると起動の失敗として扱う。</param>
        public BrowserHost(HangaOptions options, ILogger<BrowserHost>? logger = null, Func<IBrowser, Task>? afterLaunch = null)
        {
            this.options = options;
            this.afterLaunch = afterLaunch;
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

        /// <summary>同期の破棄(依存性注入のコンテナが同期で破棄される場合に備える)。</summary>
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

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
            // 帳票ごとのコンテキストには、起動引数のプロキシの除外の指定が引き継がれないため、コンテキストにも同じ指定を渡す(要件3.4)。
            // 起動し直した場合も同じ指定にする
            BrowserContextOptions? contextOptions = UsesBlackHoleProxy(options)
                ? new BrowserContextOptions { ProxyServer = BlackHoleProxy, ProxyBypassList = ProxyBypassList(options) }
                : null;
            IBrowser current = await GetBrowserAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await current.CreateBrowserContextAsync(contextOptions).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) && !(ex is HangaException))
            {
                // コンテキストを作れなかった。プロセスの終了の通知(Disconnected)がまだ届いていない場合もあるため、
                // 接続の表示によらず、この Chromium を破棄して 1 回だけ起動し直す(要件9.5)
                logger.LogWarning(ex, "ブラウザコンテキストを作れなかったため、Chromium を起動し直します。");
                await InvalidateAsync(current).ConfigureAwait(false);
                IBrowser relaunched = await GetBrowserAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await relaunched.CreateBrowserContextAsync(contextOptions).ConfigureAwait(false);
                }
                catch (Exception retryEx) when (!(retryEx is OperationCanceledException))
                {
                    throw new HangaBrowserException("Chromium を起動し直しましたが、ブラウザコンテキストを作れませんでした。", options.ChromiumExecutablePath, BrowserVersion, HangaStage.BrowserLaunch, retryEx);
                }
            }
        }

        /// <summary>使えなくなった Chromium を破棄する(既に別のスレッドが起動し直していれば何もしない)。</summary>
        private async Task InvalidateAsync(IBrowser broken)
        {
            await launchLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(browser, broken))
                {
                    await CloseBrowserAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                launchLock.Release();
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

        /// <summary>
        /// Chromium の起動引数。呼び出し元の引数に加えて、許可した外部ホスト以外への通信を、行き止まりのプロキシ(127.0.0.1:9)に向ける(要件3.4)。
        /// ページへの要求の介入は帳票のページの要求にしか効かず、別のウィンドウ(window.open・target=_blank)や WebSocket の通信は介入を通らないため、
        /// その抜け道をふさぐ多重の守り(セキュリティレビューの指摘)。Hanga が自分で応える仮想オリジンへの要求はネットワークに出ないため影響しない。
        /// 呼び出し元が --proxy-server を指定した場合(社内のプロキシが必要な環境)は加えない。
        /// </summary>
        internal static List<string> BuildArguments(HangaOptions options)
        {
            var args = new List<string>(options.ChromiumArguments);
            if (UsesBlackHoleProxy(options))
            {
                args.Add("--proxy-server=" + BlackHoleProxy);
                args.Add("--proxy-bypass-list=" + string.Join(";", ProxyBypassList(options)));

                // WebRTC の UDP の通信はプロキシを通らないため、プロキシを通らない UDP を使わせない(多重の守り。未検証)
                args.Add("--force-webrtc-ip-handling-policy=disable_non_proxied_udp");
            }

            return args;
        }

        /// <summary>
        /// 行き止まりのプロキシを使うか。呼び出し元がプロキシを指定した場合(--proxy-server・--proxy-pac-url・--proxy-auto-detect・--no-proxy-server など)は、
        /// 呼び出し元の指定を優先し、使わない(Hanga の指定で呼び出し元の指定を黙って打ち消さないため)。
        /// </summary>
        internal static bool UsesBlackHoleProxy(HangaOptions options) =>
            !options.ChromiumArguments.Any(a =>
                a.StartsWith("--proxy-", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--no-proxy-server", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// プロキシを通さない(直接つなぐ)宛先: 許可した外部ホストだけ。<c>&lt;-loopback&gt;</c> は、Chromium が既定でプロキシを通さない
        /// ループバック(localhost・127.0.0.1)も、行き止まりのプロキシに向ける指定。後ろの規則が優先されるため、先頭に置く
        /// (後ろに置くと、許可したホストが 127.0.0.1 などのループバックの場合に、行き止まりに向いてしまった)。
        /// </summary>
        internal static string[] ProxyBypassList(HangaOptions options) =>
            new[] { "<-loopback>" }.Concat(options.AllowedExternalHosts.Select(h => h.Trim()).Where(h => h.Length > 0)).ToArray();

        /// <summary>
        /// 行き止まりのプロキシ。名前解決できないホスト(.invalid は RFC 6761 で実在しないことが保証される)にし、どこにもつながらないようにする。
        /// 127.0.0.1 のポートにすると、Windows では利用者の権限で同じポートを待ち受けられ、通信を受け取れてしまうため。
        /// プロキシにつながらない場合も、Chromium は直接の接続に切り替えない(2026年10月4日、Chrome 141 で確認)。
        /// </summary>
        internal const string BlackHoleProxy = "http://hanga-blackhole.invalid:9";

        private async Task<IBrowser> LaunchAsync()
        {
            string path = options.ChromiumExecutablePath;
            if (!File.Exists(path))
            {
                throw new HangaBrowserException("Chromium の実行ファイルが見つかりません。", path, null, HangaStage.BrowserLaunch);
            }

            // プロセスごとの一時ユーザーデータフォルダ。終了時に削除する。異常終了で残った古いものは、ここで片付ける
            DeleteStaleUserDataDirs();
            userDataDir = Path.Combine(Path.GetTempPath(), "hanga-chromium-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(userDataDir);
            IBrowser? launched = null;
            try
            {
                var launchOptions = new LaunchOptions
                {
                    Headless = true,
                    ExecutablePath = path,
                    UserDataDir = userDataDir,
                    Args = BuildArguments(options).ToArray(),
                };
                launched = await Puppeteer.LaunchAsync(launchOptions).ConfigureAwait(false);
                BrowserVersion = await launched.GetVersionAsync().ConfigureAwait(false);
                LaunchCount++;
                logger.LogInformation("Chromium を起動しました(版: {Version}、場所: {Path})。", BrowserVersion, path);
                if (afterLaunch != null)
                {
                    await afterLaunch(launched).ConfigureAwait(false);
                }

                return launched;
            }
            catch (Exception ex)
            {
                if (launched != null)
                {
                    try
                    {
                        await launched.CloseAsync().ConfigureAwait(false);
                    }
                    catch (Exception closeEx)
                    {
                        logger.LogDebug(closeEx, "起動の後の確認に失敗した Chromium を終了できませんでした。");
                    }

                    launched.Dispose();
                }

                DeleteUserDataDir();
                if (ex is HangaException)
                {
                    throw;
                }

                throw new HangaBrowserException("Chromium を起動できませんでした。", path, BrowserVersion, HangaStage.BrowserLaunch, ex);
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

        /// <summary>前回までに異常終了などで残った一時ユーザーデータフォルダ(1 日以上前のもの)を削除する。</summary>
        private void DeleteStaleUserDataDirs()
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(Path.GetTempPath(), "hanga-chromium-*"))
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-1))
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "古い一時ユーザーデータフォルダを削除できませんでした。");
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
