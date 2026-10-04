using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hanga
{
    /// <summary>
    /// アプリ全体の設定(design.md「オプション」)。コードと設定ファイル(<c>appsettings.json</c>)のどちらからも指定できる(要件12.6)。
    /// 値は登録時に <see cref="Validate"/> で検証する。
    /// </summary>
    public sealed class HangaOptions
    {
        /// <summary>設定ファイルの節の名前の既定。</summary>
        public const string DefaultSectionName = "Hanga";

        /// <summary>Chromium の実行ファイルの場所(必須。要件2.2)。</summary>
        public string ChromiumExecutablePath { get; set; } = string.Empty;

        /// <summary>Chromium に渡す追加の起動引数。</summary>
        public List<string> ChromiumArguments { get; set; } = new List<string>();

        /// <summary>アプリの起動時に Chromium を起動しておく(要件10.2)。既定は最初の PDF の要求時に起動する。</summary>
        public bool LaunchOnStartup { get; set; }

        /// <summary>同時に処理する帳票の数(要件9.4)。既定は 4。</summary>
        public int MaxConcurrentRenders { get; set; } = 4;

        /// <summary>表示の完了を待つ上限時間(要件4.3)。既定は 30 秒。</summary>
        public TimeSpan RenderTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>仮想オリジン以外で取得を許すホスト(要件3.4)。既定は無し(すべて遮断)。</summary>
        public List<string> AllowedExternalHosts { get; set; } = new List<string>();

        /// <summary>外字用フォントの名前(サーバーにインストールされたフォント。要件6.1)。</summary>
        public string? GaijiFontFamily { get; set; }

        /// <summary>外字用フォントのファイル(要件6.1)。名前での指定より遅いため、名前での指定を推奨する(要件10.3)。</summary>
        public string? GaijiFontFile { get; set; }

        /// <summary>字形の無い文字を検出する(要件6.7)。既定は検出する。</summary>
        public bool DetectMissingGlyphs { get; set; } = true;

        /// <summary>警告の対象(外部への要求の遮断・スクリプトの例外・字形の無い文字)をエラーにする(要件8.7)。既定は警告にとどめる。</summary>
        public bool Strict { get; set; }

        /// <summary>帳票のページを置く仮想オリジン。既定は <c>https://hanga.invalid</c>(RFC 6761 で実在しないことが保証されたドメイン)。</summary>
        public string VirtualOrigin { get; set; } = "https://hanga.invalid";

        /// <summary>仮想オリジンへの要求 1 件の応答の大きさの上限(バイト)。既定は 50MB。</summary>
        public long MaxResponseBodyBytes { get; set; } = 50L * 1024 * 1024;

        /// <summary>
        /// 帳票 1 件で、仮想オリジンへの要求の応答の合計の上限(バイト)。既定は 200MB。
        /// 1 件ごとの上限(<see cref="MaxResponseBodyBytes"/>)だけでは、大きなファイルを多数読み込むページでメモリを使い尽くしうるため。
        /// </summary>
        public long MaxTotalResponseBytesPerReport { get; set; } = 200L * 1024 * 1024;

        /// <summary>値を検証する。誤りがあれば、すべての誤りを含めて <see cref="HangaConfigurationException"/> を投げる。</summary>
        public void Validate()
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(ChromiumExecutablePath))
            {
                errors.Add("ChromiumExecutablePath(Chromium の実行ファイルの場所)を指定してください。");
            }

            if (MaxConcurrentRenders < 1)
            {
                errors.Add($"MaxConcurrentRenders は 1 以上で指定してください(指定: {MaxConcurrentRenders})。");
            }

            if (RenderTimeout <= TimeSpan.Zero)
            {
                errors.Add($"RenderTimeout は 0 より長い時間で指定してください(指定: {RenderTimeout})。");
            }

            if (MaxResponseBodyBytes < 1)
            {
                errors.Add($"MaxResponseBodyBytes は 1 以上で指定してください(指定: {MaxResponseBodyBytes})。");
            }

            if (MaxTotalResponseBytesPerReport < 1)
            {
                errors.Add($"MaxTotalResponseBytesPerReport は 1 以上で指定してください(指定: {MaxTotalResponseBytesPerReport})。");
            }

            if (!string.IsNullOrWhiteSpace(GaijiFontFamily) && !string.IsNullOrWhiteSpace(GaijiFontFile))
            {
                errors.Add("GaijiFontFamily と GaijiFontFile は、どちらか一方だけを指定してください。");
            }

            if (!string.IsNullOrWhiteSpace(GaijiFontFile) && !File.Exists(GaijiFontFile))
            {
                // 要件6.6: フォントファイルの指定は登録時に確かめる
                errors.Add($"外字用フォントのファイルが見つかりません: {GaijiFontFile}");
            }

            if (!Uri.TryCreate(VirtualOrigin, UriKind.Absolute, out Uri? origin)
                || (origin.Scheme != Uri.UriSchemeHttps && origin.Scheme != Uri.UriSchemeHttp)
                || origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.Query))
            {
                errors.Add($"VirtualOrigin は https://ホスト名 の形で指定してください(指定: {VirtualOrigin})。");
            }

            if (AllowedExternalHosts.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add("AllowedExternalHosts に空のホスト名が含まれています。");
            }

            if (errors.Count > 0)
            {
                throw new HangaConfigurationException("Hanga の設定に誤りがあります: " + string.Join(" ", errors));
            }
        }

        /// <summary>仮想オリジン(末尾の / を除いたもの)。</summary>
        internal string NormalizedVirtualOrigin => VirtualOrigin.TrimEnd('/');
    }
}
