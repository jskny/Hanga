using System;
using System.Collections.Generic;

namespace Hanga
{
    /// <summary>PDF の生成結果(design.md「生成結果と返し方」、要件7, 8.6)。</summary>
    public sealed class HangaPdfDocument
    {
        internal HangaPdfDocument(byte[] content, IReadOnlyList<HangaWarning> warnings, string chromiumVersion, TimeSpan elapsed)
        {
            Content = content;
            Warnings = warnings;
            ChromiumVersion = chromiumVersion;
            Elapsed = elapsed;
        }

        /// <summary>PDF のバイト列。</summary>
        public byte[] Content { get; }

        /// <summary>生成中に見つかった問題(外部への要求の遮断・スクリプトの例外・字形の無い文字)。</summary>
        public IReadOnlyList<HangaWarning> Warnings { get; }

        /// <summary>生成に使った Chromium の版。</summary>
        public string ChromiumVersion { get; }

        /// <summary>生成にかかった時間(ビューの HTML 化を含む)。</summary>
        public TimeSpan Elapsed { get; }
    }

    /// <summary>PDF をブラウザで開かせるか、ダウンロードさせるか(要件7.3)。</summary>
    public enum PdfDisposition
    {
        /// <summary>ブラウザで開く(<c>Content-Disposition: inline</c>)。</summary>
        Inline,

        /// <summary>ダウンロードさせる(<c>Content-Disposition: attachment</c>)。</summary>
        Attachment,
    }
}
