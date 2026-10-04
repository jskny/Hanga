using System;
using System.IO;
using System.Threading.Tasks;
using Hanga.TestReports;
using Hanga.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hanga.Tests
{
    /// <summary>
    /// バッチのテストで共有する <see cref="HangaBatch"/>(Chromium の起動を毎回しないため)。
    /// テスト用の帳票ライブラリ(<c>Hanga.TestReports</c>)のビューを使う。
    /// </summary>
    public sealed class HangaBatchFixture : IAsyncLifetime
    {
        private HangaBatch? batch;

        /// <summary>テストの出力にコピーした、テスト用の帳票ライブラリの wwwroot。</summary>
        public static string TestReportsWwwroot => Path.Combine(AppContext.BaseDirectory, "TestReportsWwwroot");

        public HangaBatch Batch => batch ?? throw new InvalidOperationException("HangaBatch が起動していません。");

        /// <summary>テストで使う設定(Chromium の場所と起動引数)。</summary>
        public static HangaOptions NewOptions() => new HangaOptions
        {
            ChromiumExecutablePath = TestChromium.ExecutablePath,
            ChromiumArguments = { TestChromium.Arguments[0] },
        };

        /// <summary>
        /// テスト用の帳票ライブラリを使う設定。静的ファイルは、帳票ライブラリの wwwroot を Web アプリと同じ URL(/_content/Hanga.TestReports)に対応付ける。
        /// </summary>
        public static void ConfigureTestReports(HangaBatchOptions options)
        {
            options.ViewAssemblies.Add(typeof(InvoiceModel).Assembly);
            options.StaticFileMappings["/_content/Hanga.TestReports"] = TestReportsWwwroot;
            options.ConfigureServices = services => services.AddSingleton<ICompanyInfo>(new TestCompany("テスト商事"));
        }

        public static InvoiceModel Invoice(string customerName) => new InvoiceModel
        {
            CustomerName = customerName,
            Lines = { new InvoiceLine("鉛筆", 1000), new InvoiceLine("消しゴム", 500) },
        };

        public async Task InitializeAsync()
        {
            batch = await HangaBatch.StartAsync(NewOptions(), ConfigureTestReports);
        }

        public async Task DisposeAsync()
        {
            if (batch != null)
            {
                await batch.DisposeAsync();
            }
        }

        private sealed class TestCompany : ICompanyInfo
        {
            public TestCompany(string companyName)
            {
                CompanyName = companyName;
            }

            public string CompanyName { get; }
        }
    }
}
