using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Hanga.Tests
{
    /// <summary>
    /// 処理時間の計測(要件10.4)。数値目標は持たない(ベストエフォート)ため、極端に遅くないことだけを確かめ、時間は出力に記録する。
    /// 計測結果は design.md「処理時間の目安」に記録する。
    /// </summary>
    public class PerformanceTests : IClassFixture<HangaAppFactory>
    {
        private readonly HangaAppFactory factory;
        private readonly ITestOutputHelper output;

        public PerformanceTests(HangaAppFactory factory, ITestOutputHelper output)
        {
            this.factory = factory;
            this.output = output;
        }

        [Fact]
        public async Task PDF用アクションの1件あたりの時間()
        {
            using var client = factory.NewClient();
            client.DefaultRequestHeaders.Add("Cookie", await PipelineForwarderTests.SignInAsync(factory, "perf"));

            var first = Stopwatch.StartNew();
            await client.GetByteArrayAsync("/Pdf/Order"); // Chromium の起動とビューのコンパイルを含む
            first.Stop();

            var times = new long[5];
            for (int i = 0; i < times.Length; i++)
            {
                var stopwatch = Stopwatch.StartNew();
                await client.GetByteArrayAsync("/Pdf/Order");
                times[i] = stopwatch.ElapsedMilliseconds;
            }

            output.WriteLine($"初回(Chromium の起動を含む): {first.ElapsedMilliseconds} ms");
            output.WriteLine($"2回目以降: {string.Join(", ", times)} ms(平均 {times.Average():0} ms)");
            Assert.All(times, t => Assert.InRange(t, 0, 30_000));
        }
    }
}
