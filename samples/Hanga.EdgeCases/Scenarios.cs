using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hanga.EdgeCases
{
    /// <summary>
    /// エッジケースの一覧。期待は「こう動くべき」という理想の動作で書く(現状の動作に合わせない)。期待と違えば NG として表に出す。
    /// </summary>
    internal static class Scenarios
    {
        private const string SpecialText =
            "外字[𠮷] 異体字[葛\U000E0102] 絵文字[😀] 結合文字[が] ゼロ幅[​] アラビア語[مرحبا] 制御文字[\u0001] 単独のサロゲート[\uD800] 終わり";

        public static IEnumerable<Scenario> All()
        {
            // ---- 表示 ----
            yield return new Scenario("D01", "表示", "大量の明細(3000行)", "成功。複数ページで、最後の行まで出る", c =>
                c.ExpectPdfAsync("D01", c.Pdf("ManyRows", new EdgeModel { Rows = 3000 }), new[] { "品名3000", "明細の終わり" },
                    check: (info, _) => info.Pages >= 20 ? null : $"ページ数が少ない({info.Pages})"));

            yield return new Scenario("D02", "表示", "大量の明細を1ページ化(SinglePage)", "成功。最後の行まで出る。用紙の高さの上限(5000mm)を超えた分は次のページになり、SinglePageOverflow の警告", c =>
                c.ExpectPdfAsync("D02", c.Pdf("ManyRows", new EdgeModel { Rows = 3000 }, o => o.SinglePage = true), new[] { "品名3000", "明細の終わり" },
                    expectedWarning: HangaWarningKind.SinglePageOverflow));

            yield return new Scenario("D03", "表示", "明細が0行", "成功", c =>
                c.ExpectPdfAsync("D03", c.Pdf("Empty"), new[] { "明細はありません" }));

            yield return new Scenario("D04", "表示", "改行できない長い英数字(3000文字)", "成功。後ろの段落も出る", c =>
                c.ExpectPdfAsync("D04", c.Pdf("LongWord", new EdgeModel { Text = new string('A', 3000) }), new[] { "長い文字列の後" }));

            yield return new Scenario("D05", "表示", "モデルに HTML・スクリプトの文字列", "成功。文字列のまま出て、実行されない(警告なし)", c =>
                c.ExpectPdfAsync("D05", c.Pdf("Text", new EdgeModel { Text = "<script>document.title='x'</script><img src=\"https://example.com/a.png\">" }),
                    new[] { "<script>document.title='x'</script>", "(CSS適用)" }));

            yield return new Scenario("D06", "表示", "Html.Raw で外部の画像を出してしまう", "成功。外部への要求は遮断して警告", c =>
                c.ExpectPdfAsync("D06", c.Pdf("RawHtml", new EdgeModel { Text = "<img src=\"https://example.com/a.png\">" }), new[] { "Html.Raw の後" },
                    expectedWarning: HangaWarningKind.BlockedExternalRequest));

            yield return new Scenario("D07", "表示", "特殊な文字(外字・異体字・絵文字・結合文字・ゼロ幅・アラビア語・制御文字・単独のサロゲート)", "成功。字形の無い文字は警告", c =>
                c.ExpectPdfAsync("D07", c.Pdf("Text", new EdgeModel { Text = SpecialText }), new[] { "外字", "終わり" },
                    expectedWarning: HangaWarningKind.MissingGlyph));

            yield return new Scenario("D08", "表示", "非常に長い本文(10万文字)", "成功(待機の上限内)", c =>
                c.ExpectPdfAsync("D08", c.Pdf("Text", new EdgeModel { Text = string.Concat(Enumerable.Repeat("帳票の本文です。", 12500)) + "末尾" }), new[] { "末尾" }));

            // ---- 静的ファイル ----
            yield return new Scenario("S01", "静的ファイル", "日本語・空白入りのファイル名、CSS の @import と相対パスの url()、クエリ付きの URL", "成功(読み込みの失敗なし)", c =>
                c.ExpectPdfAsync("S01", c.Pdf("StaticAssets"), new[] { "(CSS適用)", "(@import適用)" }));

            yield return new Scenario("S02", "静的ファイル", "data: URL の画像", "成功(待機の上限より十分短い時間で)", c =>
                c.ExpectPdfAsync("S02", c.Pdf("DataUrl"), new[] { "dataURLの画像" },
                    check: (_, document) => document.Elapsed.TotalSeconds < EdgeContext.TimeoutSeconds / 2.0 ? null : $"時間がかかりすぎ({document.Elapsed.TotalSeconds:0.0}秒)"));

            yield return new Scenario("S03", "静的ファイル", "存在しない画像", "HangaResourceRequestException(URL と 404)", c =>
                c.ExpectThrowsAsync<HangaResourceRequestException>(() => c.Pdf("MissingImage").ToBytesAsync(),
                    ex => ex.FailedRequests.Any(f => f.Url.EndsWith("/img/no-such-image.png", StringComparison.Ordinal) && f.StatusCode == 404) ? null : "失敗した要求に画像が無い"));

            // ---- JavaScript ----
            yield return new Scenario("J01", "JavaScript", "表示時に alert()", "成功。ダイアログで止まらず、後続の処理が反映される。Dialog の警告", c =>
                c.ExpectPdfAsync("J01", c.Pdf("Alert"), new[] { "alert の後" }, expectedWarning: HangaWarningKind.Dialog));

            yield return new Scenario("J02", "JavaScript", "表示時に confirm()", "成功。ダイアログで止まらず「OK」を選んだ扱い(true)。Dialog の警告", c =>
                c.ExpectPdfAsync("J02", c.Pdf("Confirm"), new[] { "confirm の後 true" }, expectedWarning: HangaWarningKind.Dialog));

            yield return new Scenario("J03", "JavaScript", "表示時に window.print()", "成功", c =>
                c.ExpectPdfAsync("J03", c.Pdf("Print"), new[] { "print の後" }));

            yield return new Scenario("J04", "JavaScript", "表示の後に別の URL へ移動", "成功。移動を止めて元のページを PDF にし、BlockedNavigation の警告(別の内容の PDF を黙って返さない)", c =>
                c.ExpectPdfAsync("J04", c.Pdf("NavigateAway"), new[] { "移動する前のページ" }, expectedWarning: HangaWarningKind.BlockedNavigation));

            yield return new Scenario("J05", "JavaScript", "無限ループ", $"HangaTimeoutException(待機の上限 {EdgeContext.TimeoutSeconds} 秒で)", c =>
                c.ExpectThrowsAsync<HangaException>(() => c.Pdf("InfiniteLoop").ToBytesAsync(), maxSeconds: EdgeContext.TimeoutSeconds + 10));

            yield return new Scenario("J06", "JavaScript", "無限ループの直後に、同時処理数を超える件数を生成", "成功(無限ループのページが同時処理数の枠を占有し続けない)", async c =>
            {
                var pdfs = Enumerable.Range(1, 5).Select(i => c.Pdf("Text", new EdgeModel { Text = "直後" + i }).ToBytesAsync()).ToArray();
                Task all = Task.WhenAll(pdfs);
                if (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(60))) != all)
                {
                    return Check.Fail("60 秒以内に終わらない(同時処理数の枠が返っていない可能性)");
                }

                await all;
                return Check.Ok("成功(5件)");
            });

            yield return new Scenario("J07", "JavaScript", "一定間隔で値を取りに行き続ける", "HangaTimeoutException(ネットワークが静止しないため。手引きに記載すべき制約)", c =>
                c.ExpectThrowsAsync<HangaTimeoutException>(() => c.Pdf("Polling").ToBytesAsync(), maxSeconds: EdgeContext.TimeoutSeconds + 10));

            yield return new Scenario("J08", "JavaScript", "スクリプトの例外", "成功。ScriptError の警告", c =>
                c.ExpectPdfAsync("J08", c.Pdf("ScriptError"), new[] { "スクリプトの例外" }, expectedWarning: HangaWarningKind.ScriptError));

            yield return new Scenario("J09", "JavaScript", "3秒後に描画(完了条件の式で待つ)", "成功。遅れて描画した値が出る", c =>
                c.ExpectPdfAsync("J09", c.Pdf("Delayed", configure: o => o.ReadyExpression = "window.reportReady === true"), new[] { "遅れて描画した値" }));

            yield return new Scenario("J10", "JavaScript", "document.write で 500 行", "成功", c =>
                c.ExpectPdfAsync("J10", c.Pdf("DocumentWrite"), new[] { "書き出し500" }));

            // ---- ビュー ----
            yield return new Scenario("V01", "ビュー", "レイアウトを使わないビュー", "成功", c =>
                c.ExpectPdfAsync("V01", c.Pdf("NoLayout"), new[] { "レイアウトを使わないビュー" }));

            yield return new Scenario("V02", "ビュー", "必須のセクションの書き忘れ", "HangaViewRenderingException", c =>
                c.ExpectThrowsAsync<HangaViewRenderingException>(() => c.Pdf("MissingSection").ToBytesAsync()));

            yield return new Scenario("V03", "ビュー", "部分ビュー(タグヘルパーと PartialAsync)", "成功", c =>
                c.ExpectPdfAsync("V03", c.Pdf("Component", new EdgeModel { Text = "注記" }), new[] { "部分ビュー:注記", "部分ビュー:注記(非同期)" }));

            yield return new Scenario("V04", "ビュー", "エリアのビュー(パスで指定)", "成功", c =>
                c.ExpectPdfAsync("V04", new Cshtml2Pdf(c.Batch, "Reports", "~/Areas/Admin/Views/Reports/AreaReport.cshtml", new EdgeModel { Text = "管理" }), new[] { "エリアのビュー:管理" }));

            yield return new Scenario("V05", "ビュー", "POST のフォーム(偽造防止のトークン)", "成功", c =>
                c.ExpectPdfAsync("V05", c.Pdf("Form", new EdgeModel { Text = "入力値" }), new[] { "フォームの後" }));

            yield return new Scenario("V06", "ビュー", "ログインユーザー・クエリを使うビュー", "成功(値は空。バッチにはオペレーターがいない)", c =>
                c.ExpectPdfAsync("V06", c.Pdf("Operator"), new[] { "ユーザー:(なし)", "認証済み:いいえ" }));

            yield return new Scenario("V07", "ビュー", "存在しないビュー", "HangaViewNotFoundException(探した場所の一覧付き)", c =>
                c.ExpectThrowsAsync<HangaViewNotFoundException>(() => c.Pdf("NoSuchView").ToBytesAsync(),
                    ex => ex.SearchedLocations.Any() ? null : "探した場所が無い"));

            yield return new Scenario("V08", "ビュー", "モデルの型の誤り", "HangaViewRenderingException", c =>
                c.ExpectThrowsAsync<HangaViewRenderingException>(() => new Cshtml2Pdf(c.Batch, "EdgeCases", "Text", "文字列のモデル").ToBytesAsync()));

            // ---- 保存・並行・寿命 ----
            yield return new Scenario("P01", "保存", "日本語・空白入りの保存先のファイル名", "成功", async c =>
            {
                string path = c.OutputPath("請求書 2026年10月 (山田).pdf");
                await c.Pdf("Text", new EdgeModel { Text = "保存" }).SaveAsync(path);
                return File.Exists(path) ? Check.Ok("成功") : Check.Fail("ファイルが無い");
            });

            yield return new Scenario("P02", "保存", "同じ保存先に 10 件を並行して保存", "すべて成功し、保存先は正しい PDF で、一時ファイルが残らない", async c =>
            {
                string dir = Path.Combine(c.Settings.OutputDirectory, "P02");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "同じ名前.pdf");
                var tasks = Enumerable.Range(1, 10).Select(i => c.Pdf("Text", new EdgeModel { Text = "並行" + i }).SaveAsync(path)).ToArray();
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch
                {
                    // 下で集計する
                }

                var failures = tasks.Where(t => t.IsFaulted).Select(t => t.Exception!.InnerException!).ToList();
                string[] leftovers = Directory.GetFiles(dir).Where(f => f != path).ToArray();
                bool valid = File.Exists(path) && PdfInfo.Read(File.ReadAllBytes(path)).Contains("並行");
                string actual = $"成功 {10 - failures.Count} 件、失敗 {failures.Count} 件"
                    + (failures.Count > 0 ? "(" + string.Join("、", failures.Select(f => f.GetType().Name).Distinct()) + ")" : string.Empty)
                    + $"、残ったファイル {leftovers.Length} 件、保存先は{(valid ? "正しい PDF" : "不正")}";
                return failures.Count == 0 && leftovers.Length == 0 && valid ? Check.Ok(actual) : Check.Fail(actual);
            });

            yield return new Scenario("P03", "保存", "存在しないフォルダへの保存", "DirectoryNotFoundException(Hanga の例外に包まない)", c =>
                c.ExpectThrowsAsync<DirectoryNotFoundException>(() => c.Pdf("Text").SaveAsync(c.OutputPath(Path.Combine("no-such-dir", "a.pdf")))));

            yield return new Scenario("P04", "並行", "30 件を並行して生成(同時処理数 4)", "すべて成功し、値が取り違えられない", async c =>
            {
                var stopwatch = Stopwatch.StartNew();
                byte[][] pdfs = await Task.WhenAll(Enumerable.Range(1, 30).Select(i => c.Pdf("Text", new EdgeModel { Text = $"顧客{i:000}" }).ToBytesAsync()));
                int wrong = pdfs.Select((p, i) => PdfInfo.Read(p).Contains($"顧客{i + 1:000}") ? 0 : 1).Sum();
                string actual = $"成功 30 件({stopwatch.Elapsed.TotalSeconds:0.0}秒、1件あたり {stopwatch.Elapsed.TotalSeconds / 30:0.00}秒)、取り違え {wrong} 件";
                return wrong == 0 ? Check.Ok(actual) : Check.Fail(actual);
            });

            yield return new Scenario("P05", "並行", "同時処理数の枠を待っている間の取り消し", "すぐに OperationCanceledException(枠が空くのを待たない)", async c =>
            {
                var busy = Enumerable.Range(1, 4).Select(_ => c.Pdf("Delayed", configure: o => o.ReadyExpression = "window.reportReady === true").ToBytesAsync()).ToArray();
                await Task.Delay(500);
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                Check check = await c.ExpectThrowsAsync<OperationCanceledException>(() => c.Pdf("Text").ToBytesAsync(cts.Token), maxSeconds: 1.5);
                await Task.WhenAll(busy);
                return check;
            });

            yield return new Scenario("P06", "寿命", "HangaBatch を 2 つ同時に使う", "どちらも成功", async c =>
            {
                await using var second = await HangaBatch.StartAsync(EdgeContext.NewOptions(c.Settings));
                var first = c.Pdf("Text", new EdgeModel { Text = "1つ目" }).ToBytesAsync();
                var other = new Cshtml2Pdf(second, "EdgeCases", "Text", new EdgeModel { Text = "2つ目" }).ToBytesAsync();
                byte[][] pdfs = await Task.WhenAll(first, other);
                return PdfInfo.Read(pdfs[0]).Contains("1つ目") && PdfInfo.Read(pdfs[1]).Contains("2つ目") ? Check.Ok("成功") : Check.Fail("内容が違う");
            });

            yield return new Scenario("P07", "寿命", "起動と終了を 5 回繰り返す", "Chromium の一時フォルダが残らない", async c =>
            {
                int before = CountUserDataDirs();
                for (int i = 0; i < 5; i++)
                {
                    await using var batch = await HangaBatch.StartAsync(EdgeContext.NewOptions(c.Settings));
                    await new Cshtml2Pdf(batch, "EdgeCases", "Text", new EdgeModel { Text = "繰り返し" }).ToBytesAsync();
                }

                int after = CountUserDataDirs();
                string actual = $"一時フォルダ {before} → {after}";
                return after <= before ? Check.Ok(actual) : Check.Fail(actual);
            });

            yield return new Scenario("P08", "寿命", "生成中に終了する", "終了は 30 秒以内に終わり、生成中の帳票は例外で終わる(応答なしにならない)", async c =>
            {
                var batch = await HangaBatch.StartAsync(EdgeContext.NewOptions(c.Settings));
                var running = new Cshtml2Pdf(batch, "EdgeCases", "Delayed", new EdgeModel()) { }.ToBytesAsync();
                await Task.Delay(500);
                var stopwatch = Stopwatch.StartNew();
                Task dispose = batch.DisposeAsync().AsTask();
                if (await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(30))) != dispose)
                {
                    return Check.Fail("終了が 30 秒以内に終わらない");
                }

                double disposeSeconds = stopwatch.Elapsed.TotalSeconds;
                if (await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(30))) != running)
                {
                    return Check.Fail($"終了は {disposeSeconds:0.0} 秒で終わったが、生成中の帳票が終わらない");
                }

                string outcome = running.IsFaulted ? EdgeContext.Describe(running.Exception!.InnerException!) : running.IsCanceled ? "取り消し" : "成功";
                return Check.Ok($"終了 {disposeSeconds:0.0} 秒、生成中の帳票: {outcome}");
            });

            yield return new Scenario("P09", "保存", "相対パスの保存先", "現在のフォルダからの相対で保存される(タスクスケジューラでは現在のフォルダに注意)", async c =>
            {
                string relative = "hanga-edge-relative-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".pdf";
                await c.Pdf("Text").SaveAsync(relative);
                string expected = Path.Combine(Environment.CurrentDirectory, relative);
                bool exists = File.Exists(expected);
                File.Delete(expected);
                return exists ? Check.Ok("現在のフォルダに保存された: " + Environment.CurrentDirectory) : Check.Fail("現在のフォルダに無い");
            });
        }

        private static int CountUserDataDirs() =>
            Directory.GetDirectories(Path.GetTempPath(), "hanga-chromium-*").Length;
    }
}
