using System;

namespace Hanga
{
    /// <summary>帳票 1 件ごとの体裁の設定(design.md「オプション」、要件5)。</summary>
    public sealed class Cshtml2PdfOptions
    {
        /// <summary>倍率の下限(Chromium の制約)。</summary>
        public const double MinScale = 0.1;

        /// <summary>倍率の上限(Chromium の制約)。</summary>
        public const double MaxScale = 2.0;

        /// <summary>用紙サイズ。既定は A4(要件5.1)。</summary>
        public PaperSize PaperSize { get; set; } = PaperSize.A4;

        /// <summary>向き。既定は縦(要件5.1)。</summary>
        public PageOrientation Orientation { get; set; } = PageOrientation.Portrait;

        /// <summary>余白(mm)。既定は上下左右 10mm。</summary>
        public PageMargins Margins { get; set; } = PageMargins.Default;

        /// <summary>拡大縮小の倍率(0.1〜2.0)。既定は 1.0(要件5.3)。</summary>
        public double Scale { get; set; } = 1.0;

        /// <summary>内容の幅が印刷可能な幅を超えるときだけ縮小する。既定は有効(要件5.4)。</summary>
        public bool FitToPageWidth { get; set; } = true;

        /// <summary>内容の高さに合わせた 1 ページにする。既定は無効(要件5.5)。</summary>
        public bool SinglePage { get; set; }

        /// <summary>使う CSS の種類。既定は印刷用(要件5.6)。</summary>
        public CssMedia CssMedia { get; set; } = CssMedia.Print;

        /// <summary>背景色・背景画像を出力する。既定は出力する(要件5.7)。</summary>
        public bool PrintBackground { get; set; } = true;

        /// <summary>フッターにページ番号(「1 / 3」)を出す。既定は出さない(要件5.8)。</summary>
        public bool PageNumbers { get; set; }

        /// <summary>PDF の文書のタイトル。未指定ならページの <c>&lt;title&gt;</c>(要件5.9)。</summary>
        public string? Title { get; set; }

        /// <summary>表示の完了の条件として追加で待つ JavaScript の式(例: <c>window.reportReady === true</c>)。要件4.2。</summary>
        public string? ReadyExpression { get; set; }

        /// <summary>表示の完了を待つ上限時間。未指定ならアプリ全体の設定(要件4.3)。</summary>
        public TimeSpan? Timeout { get; set; }

        /// <summary>厳格な扱い。未指定ならアプリ全体の設定(要件8.7)。</summary>
        public bool? Strict { get; set; }

        /// <summary>値を検証する。誤りがあれば <see cref="HangaConfigurationException"/> を投げる。</summary>
        public void Validate()
        {
            if (PaperSize == null)
            {
                throw new HangaConfigurationException("PaperSize を指定してください。");
            }

            if (Margins == null)
            {
                throw new HangaConfigurationException("Margins を指定してください。");
            }

            if (double.IsNaN(Scale) || Scale < MinScale || Scale > MaxScale)
            {
                throw new HangaConfigurationException($"Scale は {MinScale}〜{MaxScale} で指定してください(指定: {Scale})。");
            }

            if (Margins.Top < 0 || Margins.Right < 0 || Margins.Bottom < 0 || Margins.Left < 0)
            {
                throw new HangaConfigurationException($"余白は 0 以上で指定してください(指定: {Margins})。");
            }

            (double width, double height) = PageSizeMm;
            if (Margins.Left + Margins.Right >= width || Margins.Top + Margins.Bottom >= height)
            {
                throw new HangaConfigurationException($"余白が用紙より大きいため、印刷できる範囲がありません(用紙: {PaperSize}、余白: {Margins})。");
            }

            if (Timeout.HasValue && Timeout.Value <= TimeSpan.Zero)
            {
                throw new HangaConfigurationException($"Timeout は 0 より長い時間で指定してください(指定: {Timeout})。");
            }
        }

        /// <summary>向きを反映した用紙の幅と高さ(mm)。</summary>
        public (double WidthMm, double HeightMm) PageSizeMm =>
            Orientation == PageOrientation.Landscape ? (PaperSize.HeightMm, PaperSize.WidthMm) : (PaperSize.WidthMm, PaperSize.HeightMm);

        /// <summary>実際に使う待機の上限時間(帳票ごとの指定、無ければアプリ全体の設定)。</summary>
        public TimeSpan EffectiveTimeout(HangaOptions global) => Timeout ?? global.RenderTimeout;

        /// <summary>実際に使う厳格な扱い(帳票ごとの指定、無ければアプリ全体の設定)。</summary>
        public bool EffectiveStrict(HangaOptions global) => Strict ?? global.Strict;
    }
}
