namespace Hanga
{
    /// <summary>失敗した処理の段階(要件8.2)。</summary>
    public enum HangaStage
    {
        /// <summary>設定の誤り(Chromium の場所、外字用フォントが無い等)。</summary>
        Configuration,

        /// <summary>ビューのHTML化。</summary>
        ViewRendering,

        /// <summary>Chromium の起動・Chromium への命令。</summary>
        BrowserLaunch,

        /// <summary>ページの表示。</summary>
        PageLoad,

        /// <summary>仮想オリジンへの要求(静的ファイル・API)。</summary>
        ResourceRequest,

        /// <summary>表示の完了の待機。</summary>
        Waiting,

        /// <summary>PDF の出力。</summary>
        PdfOutput,
    }
}
