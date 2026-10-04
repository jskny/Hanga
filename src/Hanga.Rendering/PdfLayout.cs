using System;
using System.Globalization;

namespace Hanga.Rendering
{
    /// <summary>
    /// 体裁の計算(Chromium を使わない部分。design.md「⑨」、要件5.1〜5.5)。
    /// 長さの単位: 用紙・余白は mm、ページの内容は CSS ピクセル(1 インチ = 96 ピクセル)。
    /// </summary>
    internal static class PdfLayout
    {
        public const double CssPixelsPerInch = 96;
        public const double MillimetersPerInch = 25.4;

        /// <summary>1 ページにする場合の高さに足す余裕(mm)。丸めの誤差で 2 ページ目に 1 行だけ送られるのを防ぐ。</summary>
        public const double SinglePageSlackMm = 1;

        public static double MmToPx(double mm) => mm / MillimetersPerInch * CssPixelsPerInch;

        public static double PxToMm(double px) => px / CssPixelsPerInch * MillimetersPerInch;

        /// <summary>印刷可能な幅(CSS ピクセル)= 用紙の幅 − 左右の余白。</summary>
        public static double PrintableWidthPx(Cshtml2PdfOptions options) =>
            MmToPx(options.PageSizeMm.WidthMm - options.Margins.Left - options.Margins.Right);

        /// <summary>
        /// 実際に使う倍率(要件5.3, 5.4)。<see cref="Cshtml2PdfOptions.FitToPageWidth"/> なら、内容の幅(倍率を掛けた後)が印刷可能な幅を超えるときだけ、
        /// 収まるまで縮小する(収まる場合は指定の倍率のまま)。Chromium の制約で 0.1〜2.0 に丸める。
        /// </summary>
        public static double ComputeScale(Cshtml2PdfOptions options, double contentWidthPx)
        {
            double scale = options.Scale;
            if (options.FitToPageWidth && contentWidthPx > 0)
            {
                double printable = PrintableWidthPx(options);
                if (contentWidthPx * scale > printable)
                {
                    scale = printable / contentWidthPx;
                }
            }

            return Math.Max(Cshtml2PdfOptions.MinScale, Math.Min(Cshtml2PdfOptions.MaxScale, scale));
        }

        /// <summary>
        /// 1 ページにする場合のページの高さ(mm。要件5.5)= 内容の高さ × 倍率 + 上下の余白 + 余裕。
        /// 用紙の上限(<see cref="PaperSize.MaxMillimeters"/>)を超える場合は上限にする(超えた分は次のページに送られる)。
        /// </summary>
        public static double SinglePageHeightMm(Cshtml2PdfOptions options, double contentHeightPx, double scale)
        {
            double height = PxToMm(contentHeightPx * scale) + options.Margins.Top + options.Margins.Bottom + SinglePageSlackMm;
            return Math.Min(PaperSize.MaxMillimeters, height);
        }

        /// <summary>1 ページにする場合に、内容の高さが用紙の上限を超えて、複数ページになるか(要件5.5。警告にする)。</summary>
        public static bool SinglePageOverflows(Cshtml2PdfOptions options, double contentHeightPx, double scale) =>
            PxToMm(contentHeightPx * scale) + options.Margins.Top + options.Margins.Bottom + SinglePageSlackMm > PaperSize.MaxMillimeters;

        /// <summary>Chromium に渡す長さの文字列(例: <c>210mm</c>)。文化圏によらず小数点を . にする。</summary>
        public static string Mm(double mm) => mm.ToString("0.###", CultureInfo.InvariantCulture) + "mm";
    }
}
