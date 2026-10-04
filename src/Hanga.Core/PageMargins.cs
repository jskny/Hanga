using System.Globalization;

namespace Hanga
{
    /// <summary>余白(mm)。要件5.1。既定は上下左右 10mm。</summary>
    public sealed class PageMargins
    {
        public PageMargins(double top, double right, double bottom, double left)
        {
            Top = top;
            Right = right;
            Bottom = bottom;
            Left = left;
        }

        /// <summary>既定の余白(上下左右 10mm)。</summary>
        public static PageMargins Default => Uniform(10);

        public double Top { get; }

        public double Right { get; }

        public double Bottom { get; }

        public double Left { get; }

        /// <summary>上下左右を同じ値にした余白。</summary>
        public static PageMargins Uniform(double mm) => new PageMargins(mm, mm, mm, mm);

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "上{0} 右{1} 下{2} 左{3}mm", Top, Right, Bottom, Left);
    }
}
