using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;

namespace Hanga
{
    /// <summary>
    /// バッチ固有の設定(batch-pdf-generation の design.md「バッチ固有の設定」)。
    /// Chromium の場所などアプリ全体の設定は、Web アプリと同じ <see cref="HangaOptions"/> で指定する。
    /// </summary>
    public sealed class HangaBatchOptions
    {
        /// <summary>
        /// ビューを持つアセンブリ(帳票ライブラリ。要件2.2)。関連するビューのアセンブリ(<c>&lt;名前&gt;.Views.dll</c>)も含める。
        /// 空なら ASP.NET Core MVC の既定の規則(バッチの実行ファイルが参照するライブラリ)でビューを探す。
        /// </summary>
        public List<Assembly> ViewAssemblies { get; } = new List<Assembly>();

        /// <summary>コンテンツのルート。既定はバッチの実行ファイルのフォルダ(<see cref="AppContext.BaseDirectory"/>)。</summary>
        public string? ContentRootPath { get; set; }

        /// <summary>
        /// 静的ファイルを置くフォルダ(Web ルート。要件3.3)。相対パスはコンテンツのルートからの相対。既定は <c>wwwroot</c>。
        /// 発行したバッチでは、帳票ライブラリの静的ファイルが <c>wwwroot/_content/&lt;ライブラリ名&gt;/</c> に置かれる。
        /// </summary>
        public string? WebRootPath { get; set; }

        /// <summary>
        /// URL のパス(<c>/</c> で始まる。例: <c>/_content/Reports</c>)と、そのパスで応答する静的ファイルのフォルダの対応の追加(要件3.3)。
        /// 相対パスのフォルダはコンテンツのルートからの相対。
        /// </summary>
        public Dictionary<string, string> StaticFileMappings { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>バッチ用の変換器の依存性注入への追加の登録(ビューが <c>@inject</c> で使うサービス、ログの出力先など。要件2.5)。</summary>
        public Action<IServiceCollection>? ConfigureServices { get; set; }

        /// <summary>
        /// アプリの名前(ビューの自動の発見と、静的Webアセットの一覧の探索に使う)。既定はバッチの実行ファイル(エントリのアセンブリ)の名前。
        /// テストから、テスト用のホスト(testhost)の代わりの名前を指定するために使う。
        /// </summary>
        internal string? ApplicationName { get; set; }

        /// <summary>アプリの名前(<see cref="ApplicationName"/>、無ければエントリのアセンブリの名前)。</summary>
        internal string ResolvedApplicationName => ApplicationName ?? Assembly.GetEntryAssembly()?.GetName().Name ?? string.Empty;

        /// <summary>コンテンツのルート(絶対パス)。</summary>
        internal string ResolvedContentRootPath => Path.GetFullPath(string.IsNullOrWhiteSpace(ContentRootPath) ? AppContext.BaseDirectory : ContentRootPath!);

        /// <summary>コンテンツのルートを基準に、フォルダの絶対パスを求める。</summary>
        internal string ResolvePath(string path) => Path.GetFullPath(Path.Combine(ResolvedContentRootPath, path));

        /// <summary>フォルダが、コンテンツのルートそのもの、またはその上位か(バッチの設定ファイル・プログラムを静的ファイルとして公開してしまう指定を拒む)。</summary>
        private bool ExposesContentRoot(string folder)
        {
            string root = Path.TrimEndingDirectorySeparator(ResolvedContentRootPath) + Path.DirectorySeparatorChar;
            string candidate = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return root.StartsWith(candidate, comparison);
        }

        /// <summary>値を検証する。誤りがあれば、すべての誤りを含めて <see cref="HangaConfigurationException"/> を投げる。</summary>
        internal void Validate()
        {
            var errors = new List<string>();
            if (!Directory.Exists(ResolvedContentRootPath))
            {
                errors.Add($"ContentRootPath のフォルダが見つかりません: {ResolvedContentRootPath}");
            }

            if (!string.IsNullOrWhiteSpace(WebRootPath) && !Directory.Exists(ResolvePath(WebRootPath!)))
            {
                errors.Add($"WebRootPath のフォルダが見つかりません: {ResolvePath(WebRootPath!)}");
            }

            if (!string.IsNullOrWhiteSpace(WebRootPath) && ExposesContentRoot(ResolvePath(WebRootPath!)))
            {
                errors.Add($"WebRootPath に、コンテンツのルート(またはその上位)のフォルダは指定できません(設定ファイルやプログラムが帳票のページから読めてしまうため): {ResolvePath(WebRootPath!)}");
            }

            foreach (var mapping in StaticFileMappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.Key) || !mapping.Key.StartsWith("/", StringComparison.Ordinal))
                {
                    errors.Add($"StaticFileMappings の URL のパスは / で始めてください(指定: {mapping.Key})。");
                }

                if (string.IsNullOrWhiteSpace(mapping.Value) || !Directory.Exists(ResolvePath(mapping.Value)))
                {
                    errors.Add($"StaticFileMappings のフォルダが見つかりません({mapping.Key}): {mapping.Value}");
                }
                else if (ExposesContentRoot(ResolvePath(mapping.Value)))
                {
                    errors.Add($"StaticFileMappings に、コンテンツのルート(またはその上位)のフォルダは指定できません(設定ファイルやプログラムが帳票のページから読めてしまうため)({mapping.Key}): {mapping.Value}");
                }
            }

            if (ViewAssemblies.Any(a => a == null))
            {
                errors.Add("ViewAssemblies に null が含まれています。");
            }

            foreach (Assembly assembly in ViewAssemblies.Where(a => a != null))
            {
                try
                {
                    // 関連するビューのアセンブリ(<名前>.Views.dll)が配置されていない場合を、起動時に知らせる
                    RelatedAssemblyAttribute.GetRelatedAssemblies(assembly, throwOnError: true);
                }
                catch (Exception ex) when (ex is FileNotFoundException || ex is FileLoadException || ex is BadImageFormatException)
                {
                    errors.Add($"ViewAssemblies の {assembly.GetName().Name} に関連するビューのアセンブリを読み込めません: {ex.Message}");
                }
            }

            if (errors.Count > 0)
            {
                throw new HangaConfigurationException("Hanga のバッチの設定に誤りがあります: " + string.Join(" ", errors));
            }
        }
    }
}
