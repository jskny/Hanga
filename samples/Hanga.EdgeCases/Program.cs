using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;

namespace Hanga.EdgeCases
{
    /// <summary>
    /// エッジケースの検証(samples/Hanga.EdgeCases/README.md)。各ケースを HangaBatch で実行し、期待どおりの結果かを表にして出す。
    /// 終了コードは、期待と違う結果(応答なしを含む)の件数。
    /// </summary>
    public static class Program
    {
        /// <summary>1 ケースの上限。これを超えたら「応答なし」とする(Hanga の待機の上限とは別の、検証側の見張り)。</summary>
        private static readonly TimeSpan HangLimit = TimeSpan.FromSeconds(90);

        public static async Task<int> Main(string[] args)
        {
            Settings settings;
            try
            {
                settings = Settings.Parse(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine("使い方(終了コード: 期待と違う結果の件数。引数の誤りは -1): dotnet run --project samples/Hanga.EdgeCases -- --chromium <Chromium> [--arg <引数>]... [--gaiji-font <名前>] [--out <フォルダ>] [--only <ケースの番号の先頭>]");
                return -1;
            }

            Directory.CreateDirectory(settings.OutputDirectory);
            var results = new List<Result>();
            await using (var context = await EdgeContext.StartAsync(settings))
            {
                foreach (Scenario scenario in Scenarios.All().Where(s => settings.Only == null || s.Id.StartsWith(settings.Only, StringComparison.OrdinalIgnoreCase)))
                {
                    Console.Write($"{scenario.Id} {scenario.Title} ... ");
                    Result result = await RunAsync(scenario, context);
                    results.Add(result);
                    Console.WriteLine($"{(result.AsExpected ? "OK" : "NG")}({result.Seconds:0.0}秒) {result.Actual}");
                }
            }

            string report = Format(results, settings);
            File.WriteAllText(Path.Combine(settings.OutputDirectory, "results.md"), report, new UTF8Encoding(false));
            Console.WriteLine();
            Console.WriteLine(report);
            return results.Count(r => !r.AsExpected);
        }

        private static async Task<Result> RunAsync(Scenario scenario, EdgeContext context)
        {
            var stopwatch = Stopwatch.StartNew();
            Task<Check> run = Task.Run(() => scenario.Run(context));
            Task finished = await Task.WhenAny(run, Task.Delay(HangLimit));
            if (finished != run)
            {
                // 打ち切ったケースの例外は観測済みにする(後のケースの結果には、同時処理数の枠を使い続けることで影響しうる。その旨を表に出す)
                _ = run.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return new Result(scenario, false, $"応答なし({HangLimit.TotalSeconds:0}秒以上。以降のケースに影響しうる)", stopwatch.Elapsed.TotalSeconds);
            }

            Check check;
            try
            {
                check = await run;
            }
            catch (Exception ex)
            {
                check = Check.Fail($"検証の想定外の例外: {ex.GetType().Name}: {OneLine(ex.Message)}");
            }

            return new Result(scenario, check.AsExpected, check.Actual, stopwatch.Elapsed.TotalSeconds);
        }

        private static string Format(IReadOnlyList<Result> results, Settings settings)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# エッジケースの検証結果");
            builder.AppendLine();
            builder.AppendLine($"- 日時: {DateTime.Now:yyyy-MM-dd HH:mm}");
            builder.AppendLine($"- Chromium: {settings.ChromiumPath}");
            builder.AppendLine($"- 外字用フォント: {settings.GaijiFont ?? "(なし)"}");
            builder.AppendLine($"- 期待どおり: {results.Count(r => r.AsExpected)} / {results.Count} 件");
            builder.AppendLine();
            builder.AppendLine("| 番号 | 分類 | ケース | 期待 | 結果 | 判定 | 秒 |");
            builder.AppendLine("|---|---|---|---|---|---|---|");
            foreach (Result r in results)
            {
                builder.AppendLine($"| {r.Scenario.Id} | {r.Scenario.Category} | {r.Scenario.Title} | {Cell(r.Scenario.Expected)} | {Cell(r.Actual)} | {(r.AsExpected ? "OK" : "**NG**")} | {r.Seconds:0.0} |");
            }

            return builder.ToString();
        }

        private static string Cell(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

        internal static string OneLine(string value)
        {
            string line = value.Replace("\r", " ").Replace("\n", " ");
            return line.Length <= 160 ? line : line.Substring(0, 160) + "…";
        }
    }

    /// <summary>コマンドラインの設定。</summary>
    internal sealed class Settings
    {
        public string ChromiumPath { get; private set; } = string.Empty;

        public List<string> ChromiumArguments { get; } = new List<string>();

        public string? GaijiFont { get; private set; }

        public string OutputDirectory { get; private set; } = Path.Combine(Path.GetTempPath(), "hanga-edge-cases");

        public string? Only { get; private set; }

        public static Settings Parse(string[] args)
        {
            var settings = new Settings();
            for (int i = 0; i < args.Length; i++)
            {
                string value = i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"{args[i]} の値がありません。");
                switch (args[i])
                {
                    case "--chromium": settings.ChromiumPath = value; break;
                    case "--arg": settings.ChromiumArguments.Add(value); break;
                    case "--gaiji-font": settings.GaijiFont = value; break;
                    case "--out": settings.OutputDirectory = Path.GetFullPath(value); break;
                    case "--only": settings.Only = value; break;
                    default: throw new ArgumentException($"知らない引数です: {args[i]}");
                }

                i++;
            }

            if (string.IsNullOrWhiteSpace(settings.ChromiumPath))
            {
                throw new ArgumentException("--chromium で Chromium の実行ファイルを指定してください。");
            }

            return settings;
        }
    }

    /// <summary>1 ケース。<see cref="Run"/> は期待どおりかの判定を返す。</summary>
    internal sealed class Scenario
    {
        public Scenario(string id, string category, string title, string expected, Func<EdgeContext, Task<Check>> run)
        {
            Id = id;
            Category = category;
            Title = title;
            Expected = expected;
            Run = run;
        }

        public string Id { get; }

        public string Category { get; }

        public string Title { get; }

        public string Expected { get; }

        public Func<EdgeContext, Task<Check>> Run { get; }
    }

    /// <summary>判定。</summary>
    internal sealed class Check
    {
        private Check(bool asExpected, string actual)
        {
            AsExpected = asExpected;
            Actual = actual;
        }

        public bool AsExpected { get; }

        public string Actual { get; }

        public static Check Ok(string actual) => new Check(true, actual);

        public static Check Fail(string actual) => new Check(false, actual);
    }

    internal sealed class Result
    {
        public Result(Scenario scenario, bool asExpected, string actual, double seconds)
        {
            Scenario = scenario;
            AsExpected = asExpected;
            Actual = actual;
            Seconds = seconds;
        }

        public Scenario Scenario { get; }

        public bool AsExpected { get; }

        public string Actual { get; }

        public double Seconds { get; }
    }

    /// <summary>PDF の読み取り結果(文字列は空白を除いて比べる。PDF から取り出すと単語の間に空白が入るため)。</summary>
    internal sealed class PdfInfo
    {
        private PdfInfo(int pages, string text, double firstPageHeight)
        {
            Pages = pages;
            Text = text;
            FirstPageHeight = firstPageHeight;
        }

        public int Pages { get; }

        /// <summary>全ページの文字列(空白を除いたもの)。</summary>
        public string Text { get; }

        /// <summary>1 ページ目の高さ(pt)。</summary>
        public double FirstPageHeight { get; }

        public static PdfInfo Read(byte[] pdf)
        {
            using var document = PdfDocument.Open(pdf);
            var text = new StringBuilder();
            double height = 0;
            foreach (var page in document.GetPages())
            {
                if (height == 0)
                {
                    height = page.Height;
                }

                foreach (var word in page.GetWords())
                {
                    text.Append(word.Text);
                }
            }

            return new PdfInfo(document.NumberOfPages, Squash(text.ToString()), height);
        }

        public bool Contains(string value) => Text.Contains(Squash(value), StringComparison.Ordinal);

        public static string Squash(string value) => new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
    }
}
