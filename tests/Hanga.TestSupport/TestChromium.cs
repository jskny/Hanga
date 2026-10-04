using System;
using System.IO;

namespace Hanga.TestSupport
{
    /// <summary>結合テストで使う Chromium の実行ファイルの場所。</summary>
    public static class TestChromium
    {
        /// <summary>この開発環境(Claude Code on the web)にプリインストールされている Chromium。</summary>
        private const string PreinstalledPath = "/opt/pw-browsers/chromium";

        /// <summary>
        /// 環境変数 <c>HANGA_TEST_CHROMIUM</c>、無ければこの開発環境のプリインストール版を返す。
        /// どちらも無い場合は、テストを飛ばさずに失敗させる(Chromium の無い環境で結合テストが通ったことにしないため)。
        /// </summary>
        public static string ExecutablePath
        {
            get
            {
                string? fromEnv = Environment.GetEnvironmentVariable("HANGA_TEST_CHROMIUM");
                if (!string.IsNullOrEmpty(fromEnv))
                {
                    return fromEnv!;
                }

                string preinstalled = Path.Combine(PreinstalledPath, "chrome-linux", "chrome");
                if (File.Exists(preinstalled))
                {
                    return preinstalled;
                }

                // /opt/pw-browsers/chromium はシンボリックリンクで、実体は chromium-<番号>/chrome-linux/chrome
                if (Directory.Exists("/opt/pw-browsers"))
                {
                    foreach (string dir in Directory.GetDirectories("/opt/pw-browsers", "chromium-*"))
                    {
                        string candidate = Path.Combine(dir, "chrome-linux", "chrome");
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }

                throw new InvalidOperationException(
                    "結合テストに使う Chromium が見つかりません。環境変数 HANGA_TEST_CHROMIUM に Chromium の実行ファイルの場所を設定してください。");
            }
        }
    }
}
