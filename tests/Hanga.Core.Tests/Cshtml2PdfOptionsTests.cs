using System;
using Xunit;

namespace Hanga.Core.Tests
{
    public class Cshtml2PdfOptionsTests
    {
        [Fact]
        public void 既定値は設計書どおり()
        {
            var o = new Cshtml2PdfOptions();
            Assert.Equal(PaperSize.A4, o.PaperSize);           // 要件5.1: A4 縦
            Assert.Equal(PageOrientation.Portrait, o.Orientation);
            Assert.Equal(10, o.Margins.Top);
            Assert.Equal(10, o.Margins.Left);
            Assert.Equal(1.0, o.Scale);
            Assert.True(o.FitToPageWidth);                     // 要件5.4: 既定で有効
            Assert.False(o.SinglePage);
            Assert.Equal(CssMedia.Print, o.CssMedia);          // 要件5.6: 既定は印刷用
            Assert.True(o.PrintBackground);                    // 要件5.7
            Assert.False(o.PageNumbers);
            Assert.Null(o.Timeout);
            Assert.Null(o.Strict);
            o.Validate();
        }

        [Fact]
        public void 横向きでは幅と高さを入れ替える()
        {
            var o = new Cshtml2PdfOptions { Orientation = PageOrientation.Landscape };
            Assert.Equal((297d, 210d), o.PageSizeMm);
            o.PaperSize = PaperSize.B5;
            Assert.Equal((257d, 182d), o.PageSizeMm);
        }

        [Theory]
        [InlineData(0.09)]
        [InlineData(2.01)]
        [InlineData(double.NaN)]
        public void 倍率が範囲外ならエラー(double scale)
        {
            var o = new Cshtml2PdfOptions { Scale = scale };
            Assert.Throws<HangaConfigurationException>(() => o.Validate());
        }

        [Fact]
        public void 余白が負ならエラー()
        {
            var o = new Cshtml2PdfOptions { Margins = new PageMargins(-1, 0, 0, 0) };
            Assert.Throws<HangaConfigurationException>(() => o.Validate());
        }

        [Fact]
        public void 余白が用紙より大きければエラー()
        {
            var o = new Cshtml2PdfOptions { PaperSize = PaperSize.A5, Margins = PageMargins.Uniform(80) };
            Assert.Throws<HangaConfigurationException>(() => o.Validate());
        }

        [Fact]
        public void 帳票ごとの指定が無ければアプリ全体の設定を使う()
        {
            // 要件4.3, 8.7
            var global = new HangaOptions { RenderTimeout = TimeSpan.FromSeconds(12), Strict = true };
            var o = new Cshtml2PdfOptions();
            Assert.Equal(TimeSpan.FromSeconds(12), o.EffectiveTimeout(global));
            Assert.True(o.EffectiveStrict(global));
        }

        [Fact]
        public void 帳票ごとの指定はアプリ全体の設定を上書きする()
        {
            var global = new HangaOptions { RenderTimeout = TimeSpan.FromSeconds(12), Strict = true };
            var o = new Cshtml2PdfOptions { Timeout = TimeSpan.FromSeconds(5), Strict = false };
            Assert.Equal(TimeSpan.FromSeconds(5), o.EffectiveTimeout(global));
            Assert.False(o.EffectiveStrict(global));
        }
    }
}
