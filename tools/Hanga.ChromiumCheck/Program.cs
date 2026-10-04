using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Hanga.ChromiumCheck
{
    /// <summary>Chromium のバージョンアップの検証ツール(要件11、docs/Chromium更新前の検証手順.md)。</summary>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            CheckOptions options;
            try
            {
                options = CheckOptions.Parse(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine(CheckOptions.Usage);
                return 2;
            }

            try
            {
                string version = await new SampleRunner(options).CollectAsync(options.Output);
                Console.WriteLine($"Chromium の版: {version}");
                Console.WriteLine($"サンプルの帳票の PDF を保存しました: {Path.GetFullPath(options.Output)}");
                if (options.Command == "baseline")
                {
                    Console.WriteLine("基準を作りました。Chromium を更新した後に compare で比べてください。");
                    return 0;
                }

                string baselineVersion = File.Exists(Path.Combine(options.Baseline!, "chromium-version.txt"))
                    ? File.ReadAllText(Path.Combine(options.Baseline!, "chromium-version.txt")).Trim()
                    : "不明";
                var differences = new ReportComparer().Compare(options.Baseline!, options.Output);
                var report = new StringBuilder()
                    .AppendLine("Hanga Chromium 検証の結果")
                    .AppendLine($"基準: {Path.GetFullPath(options.Baseline!)}(Chromium {baselineVersion})")
                    .AppendLine($"今回: {Path.GetFullPath(options.Output)}(Chromium {version})")
                    .AppendLine();
                if (differences.Count == 0)
                {
                    report.AppendLine("違いはありませんでした(ページ数・ページサイズ・文字列・文字の位置とフォント)。");
                }
                else
                {
                    report.AppendLine($"{differences.Count} 件の違いがありました。今回の PDF を開いて内容を確かめ、問題が無ければ今回の結果を新しい基準にしてください。");
                    foreach (string difference in differences)
                    {
                        report.AppendLine("- " + difference);
                    }
                }

                string text = report.ToString();
                Console.Write(text);
                await File.WriteAllTextAsync(Path.Combine(options.Output, "report.txt"), text);
                return differences.Count == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("検証を実行できませんでした: " + ex.Message);
                Console.Error.WriteLine(ex);
                return 2;
            }
        }
    }
}
