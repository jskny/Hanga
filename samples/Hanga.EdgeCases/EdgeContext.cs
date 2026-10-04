using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hanga.EdgeCases
{
    /// <summary>
    /// ケースが共有する HangaBatch と、判定の補助。待機の上限は 10 秒(既定の 30 秒より短くし、時間切れのケースを速く終える)。
    /// </summary>
    internal sealed class EdgeContext : IAsyncDisposable
    {
        public const int TimeoutSeconds = 10;

        private EdgeContext(Settings settings, HangaBatch batch)
        {
            Settings = settings;
            Batch = batch;
        }

        public Settings Settings { get; }

        public HangaBatch Batch { get; }

        public static async Task<EdgeContext> StartAsync(Settings settings)
        {
            return new EdgeContext(settings, await HangaBatch.StartAsync(NewOptions(settings)));
        }

        public static HangaOptions NewOptions(Settings settings)
        {
            var options = new HangaOptions
            {
                ChromiumExecutablePath = settings.ChromiumPath,
                RenderTimeout = TimeSpan.FromSeconds(TimeoutSeconds),
                MaxConcurrentRenders = 4,
                GaijiFontFamily = settings.GaijiFont,
            };
            options.ChromiumArguments.AddRange(settings.ChromiumArguments);
            return options;
        }

        public string OutputPath(string fileName) => Path.Combine(Settings.OutputDirectory, fileName);

        public Cshtml2Pdf Pdf(string view, EdgeModel? model = null, Action<Cshtml2PdfOptions>? configure = null)
        {
            var pdf = new Cshtml2Pdf(Batch, "EdgeCases", view, model ?? new EdgeModel());
            configure?.Invoke(pdf.Options);
            return pdf;
        }

        /// <summary>PDF ができ、文字列を含み、追加の判定(<paramref name="check"/>)が null を返すことを期待する。PDF は出力のフォルダに保存する。</summary>
        public async Task<Check> ExpectPdfAsync(
            string id,
            Cshtml2Pdf pdf,
            string[] mustContain,
            HangaWarningKind? expectedWarning = null,
            Func<PdfInfo, HangaPdfDocument, string?>? check = null,
            CancellationToken cancellationToken = default)
        {
            HangaPdfDocument document;
            try
            {
                document = await pdf.GenerateAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                return Check.Fail("例外: " + Describe(ex));
            }

            await File.WriteAllBytesAsync(OutputPath(id + ".pdf"), document.Content, CancellationToken.None);
            var info = PdfInfo.Read(document.Content);
            string warnings = document.Warnings.Count == 0
                ? "警告なし"
                : "警告: " + string.Join("、", document.Warnings.GroupBy(w => w.Kind).Select(g => $"{g.Key}×{g.Count()}"));
            string actual = $"成功({info.Pages}ページ、{warnings})";

            string[] missing = mustContain.Where(t => !info.Contains(t)).ToArray();
            if (missing.Length > 0)
            {
                return Check.Fail(actual + "。PDF に無い文字列: " + string.Join("、", missing));
            }

            if (expectedWarning.HasValue && !document.Warnings.Any(w => w.Kind == expectedWarning.Value))
            {
                return Check.Fail(actual + $"。期待した警告 {expectedWarning} が無い");
            }

            if (!expectedWarning.HasValue && document.Warnings.Count > 0)
            {
                return Check.Fail(actual + "。想定外の警告: " + Program.OneLine(string.Join(" / ", document.Warnings.Select(w => w.Message + " " + w.Detail))));
            }

            string? problem = check?.Invoke(info, document);
            return problem == null ? Check.Ok(actual) : Check.Fail(actual + "。" + problem);
        }

        /// <summary><typeparamref name="TException"/>(またはその派生)の例外になることを期待する。</summary>
        public async Task<Check> ExpectThrowsAsync<TException>(Func<Task> action, Func<TException, string?>? check = null, double? maxSeconds = null)
            where TException : Exception
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await action();
                return Check.Fail($"例外にならなかった(期待: {typeof(TException).Name})");
            }
            catch (TException ex)
            {
                string actual = "例外: " + Describe(ex);
                if (maxSeconds.HasValue && stopwatch.Elapsed.TotalSeconds > maxSeconds.Value)
                {
                    return Check.Fail(actual + $"。時間がかかりすぎ({stopwatch.Elapsed.TotalSeconds:0.0}秒 > {maxSeconds}秒)");
                }

                string? problem = check?.Invoke(ex);
                return problem == null ? Check.Ok(actual) : Check.Fail(actual + "。" + problem);
            }
            catch (Exception ex)
            {
                return Check.Fail("想定と違う例外: " + Describe(ex));
            }
        }

        public static string Describe(Exception ex) => $"{ex.GetType().Name}: {Program.OneLine(ex.Message)}";

        public ValueTask DisposeAsync() => Batch.DisposeAsync();
    }
}
