using System;
using System.IO;
using Xunit;

namespace Hanga.Core.Tests
{
    public class HangaOptionsTests
    {
        private static HangaOptions Valid() => new HangaOptions { ChromiumExecutablePath = "/usr/bin/chrome" };

        [Fact]
        public void 既定値は設計書どおり()
        {
            var o = new HangaOptions();
            Assert.Equal(4, o.MaxConcurrentRenders);                  // 要件9.4
            Assert.Equal(TimeSpan.FromSeconds(30), o.RenderTimeout);  // 要件4.3
            Assert.False(o.Strict);                                   // 要件8.7: 既定は警告にとどめる
            Assert.True(o.DetectMissingGlyphs);                       // 要件6.7
            Assert.False(o.LaunchOnStartup);                          // 要件10.2
            Assert.Empty(o.AllowedExternalHosts);                     // 要件3.4: 既定はすべて遮断
            Assert.Equal("https://hanga.invalid", o.VirtualOrigin);
        }

        [Fact]
        public void 正しい設定は検証を通る()
        {
            Valid().Validate();
        }

        [Fact]
        public void Chromiumの場所が無ければエラー()
        {
            var ex = Assert.Throws<HangaConfigurationException>(() => new HangaOptions().Validate());
            Assert.Contains("ChromiumExecutablePath", ex.Message);
            Assert.Equal(HangaStage.Configuration, ex.Stage);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void 同時処理数が1未満ならエラー(int value)
        {
            var o = Valid();
            o.MaxConcurrentRenders = value;
            var ex = Assert.Throws<HangaConfigurationException>(() => o.Validate());
            Assert.Contains("MaxConcurrentRenders", ex.Message);
        }

        [Fact]
        public void 待機の上限時間が0以下ならエラー()
        {
            var o = Valid();
            o.RenderTimeout = TimeSpan.Zero;
            Assert.Throws<HangaConfigurationException>(() => o.Validate());
        }

        [Fact]
        public void 複数の誤りはまとめて報告する()
        {
            var o = new HangaOptions { MaxConcurrentRenders = 0, RenderTimeout = TimeSpan.Zero };
            var ex = Assert.Throws<HangaConfigurationException>(() => o.Validate());
            Assert.Contains("ChromiumExecutablePath", ex.Message);
            Assert.Contains("MaxConcurrentRenders", ex.Message);
            Assert.Contains("RenderTimeout", ex.Message);
        }

        [Fact]
        public void 外字用フォントのファイルが無ければ登録時にエラー()
        {
            // 要件6.6: フォントファイルの指定は登録時に確かめる
            var o = Valid();
            o.GaijiFontFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ttf");
            var ex = Assert.Throws<HangaConfigurationException>(() => o.Validate());
            Assert.Contains("外字用フォント", ex.Message);
        }

        [Fact]
        public void 外字用フォントの名前とファイルの両方の指定はエラー()
        {
            string file = Path.GetTempFileName();
            try
            {
                var o = Valid();
                o.GaijiFontFamily = "IPAmj明朝";
                o.GaijiFontFile = file;
                Assert.Throws<HangaConfigurationException>(() => o.Validate());
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Theory]
        [InlineData("hanga.invalid")]
        [InlineData("ftp://hanga.invalid")]
        [InlineData("https://hanga.invalid/report")]
        [InlineData("https://hanga.invalid/?a=1")]
        public void 仮想オリジンの形が誤っていればエラー(string origin)
        {
            var o = Valid();
            o.VirtualOrigin = origin;
            Assert.Throws<HangaConfigurationException>(() => o.Validate());
        }

        [Fact]
        public void 仮想オリジンの末尾のスラッシュは取り除く()
        {
            var o = Valid();
            o.VirtualOrigin = "https://hanga.invalid/";
            o.Validate();
            Assert.Equal("https://hanga.invalid", o.NormalizedVirtualOrigin);
        }

        [Fact]
        public void 許可ホストに空の名前があればエラー()
        {
            var o = Valid();
            o.AllowedExternalHosts.Add(" ");
            Assert.Throws<HangaConfigurationException>(() => o.Validate());
        }
    }
}
