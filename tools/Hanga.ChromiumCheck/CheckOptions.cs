using System;
using System.Collections.Generic;

namespace Hanga.ChromiumCheck
{
    /// <summary>コマンドラインの引数。</summary>
    internal sealed class CheckOptions
    {
        public string Command { get; private set; } = string.Empty;

        public string Chromium { get; private set; } = string.Empty;

        public List<string> ChromiumArguments { get; } = new List<string>();

        public string? GaijiFont { get; private set; }

        public string Output { get; private set; } = string.Empty;

        public string? Baseline { get; private set; }

        public const string Usage = @"使い方:
  基準を作る:   Hanga.ChromiumCheck baseline --chromium <Chromium の実行ファイル> --out <基準を保存するフォルダ> [--arg <起動引数>]... [--gaiji-font <外字用フォントの名前>]
  基準と比べる: Hanga.ChromiumCheck compare  --chromium <Chromium の実行ファイル> --baseline <基準のフォルダ> --out <結果を保存するフォルダ> [--arg <起動引数>]... [--gaiji-font <外字用フォントの名前>]

終了コード: 0 = 違いなし(baseline は成功)、1 = 違いあり、2 = 実行できなかった";

        public static CheckOptions Parse(string[] args)
        {
            if (args.Length == 0 || (args[0] != "baseline" && args[0] != "compare"))
            {
                throw new ArgumentException("最初の引数に baseline か compare を指定してください。");
            }

            var options = new CheckOptions { Command = args[0] };
            for (int i = 1; i < args.Length; i++)
            {
                string value = i + 1 < args.Length ? args[i + 1] : throw new ArgumentException(args[i] + " の値がありません。");
                switch (args[i])
                {
                    case "--chromium": options.Chromium = value; break;
                    case "--out": options.Output = value; break;
                    case "--baseline": options.Baseline = value; break;
                    case "--arg": options.ChromiumArguments.Add(value); break;
                    case "--gaiji-font": options.GaijiFont = value; break;
                    default: throw new ArgumentException("知らない引数です: " + args[i]);
                }

                i++;
            }

            if (string.IsNullOrEmpty(options.Chromium) || string.IsNullOrEmpty(options.Output))
            {
                throw new ArgumentException("--chromium と --out を指定してください。");
            }

            if (options.Command == "compare" && string.IsNullOrEmpty(options.Baseline))
            {
                throw new ArgumentException("compare には --baseline を指定してください。");
            }

            return options;
        }
    }
}
