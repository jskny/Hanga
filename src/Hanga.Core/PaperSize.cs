using System;
using System.Globalization;

namespace Hanga
{
    /// <summary>
    /// 用紙サイズ(縦向きの幅・高さ。mm)。要件5.1。
    /// PuppeteerSharp の用紙の定数は版によって寸法が異なるため使わず、mm で持つ(要件5.2)。
    /// </summary>
    public sealed class PaperSize : IEquatable<PaperSize>
    {
        /// <summary>指定できる寸法の上限(mm)。Chromium に極端な寸法を渡さないための安全弁。</summary>
        public const double MaxMillimeters = 5000;

        private PaperSize(string name, double widthMm, double heightMm)
        {
            Name = name;
            WidthMm = widthMm;
            HeightMm = heightMm;
        }

        public static PaperSize A3 { get; } = new PaperSize("A3", 297, 420);

        public static PaperSize A4 { get; } = new PaperSize("A4", 210, 297);

        public static PaperSize A5 { get; } = new PaperSize("A5", 148, 210);

        /// <summary>B4(JIS)。日本の業務で使う JIS B列。</summary>
        public static PaperSize B4 { get; } = new PaperSize("B4", 257, 364);

        /// <summary>B5(JIS)。日本の業務で使う JIS B列。</summary>
        public static PaperSize B5 { get; } = new PaperSize("B5", 182, 257);

        public static PaperSize Letter { get; } = new PaperSize("Letter", 215.9, 279.4);

        public static PaperSize Legal { get; } = new PaperSize("Legal", 215.9, 355.6);

        /// <summary>名前(A4 等。<see cref="Custom"/> の場合は "Custom")。</summary>
        public string Name { get; }

        /// <summary>縦向きの幅(mm)。</summary>
        public double WidthMm { get; }

        /// <summary>縦向きの高さ(mm)。</summary>
        public double HeightMm { get; }

        /// <summary>幅と高さ(mm)を指定した用紙。</summary>
        public static PaperSize Custom(double widthMm, double heightMm)
        {
            if (!IsValidLength(widthMm) || !IsValidLength(heightMm))
            {
                throw new HangaConfigurationException(
                    $"用紙の幅と高さは 0 より大きく {MaxMillimeters}mm 以下で指定してください(指定: {widthMm} × {heightMm}mm)。");
            }

            return new PaperSize("Custom", widthMm, heightMm);
        }

        /// <summary>名前(A3・A4・A5・B4・B5・Letter・Legal。大文字小文字を区別しない)から用紙を得る。設定ファイルからの読み込みに使う。</summary>
        public static PaperSize FromName(string name)
        {
            switch (name.Trim().ToUpperInvariant())
            {
                case "A3": return A3;
                case "A4": return A4;
                case "A5": return A5;
                case "B4": return B4;
                case "B5": return B5;
                case "LETTER": return Letter;
                case "LEGAL": return Legal;
                default:
                    throw new HangaConfigurationException($"用紙サイズ '{name}' は使えません。A3・A4・A5・B4・B5・Letter・Legal のいずれか、または PaperSize.Custom を使ってください。");
            }
        }

        public bool Equals(PaperSize? other) =>
            other != null && Name == other.Name && WidthMm.Equals(other.WidthMm) && HeightMm.Equals(other.HeightMm);

        public override bool Equals(object? obj) => Equals(obj as PaperSize);

        public override int GetHashCode() => HashCode.Combine(Name, WidthMm, HeightMm);

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "{0} ({1} × {2}mm)", Name, WidthMm, HeightMm);

        private static bool IsValidLength(double mm) => !double.IsNaN(mm) && mm > 0 && mm <= MaxMillimeters;
    }

    /// <summary>用紙の向き(要件5.1)。</summary>
    public enum PageOrientation
    {
        Portrait,
        Landscape,
    }

    /// <summary>使う CSS の種類(要件5.6)。</summary>
    public enum CssMedia
    {
        /// <summary>印刷用の CSS(<c>@media print</c>)。ブラウザで印刷したときと同じ結果になる。</summary>
        Print,

        /// <summary>画面表示用の CSS。</summary>
        Screen,
    }
}
