using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PuppeteerSharp;

namespace Hanga.Rendering
{
    /// <summary>
    /// 外字用フォントの適用・異体字の包み込み・字形の無い文字の検出(design.md「⑧」、要件6)。スレッドセーフ(状態は読み取り専用)。
    /// 表示の完了の後(API から取得した値が描画された後)に実行する。前に行うと、後から描画された文字が対象から漏れるため。
    /// </summary>
    internal sealed class GlyphSupport
    {
        /// <summary>外字用フォントを使う範囲(CJK 統合漢字拡張B以降・私用領域)。通常のフォントに無いことが多い範囲に絞り、外字の無いページでは読み込ませない(要件6.3)。</summary>
        internal const string GaijiUnicodeRange = "U+20000-3134F, U+E000-F8FF, U+F0000-10FFFF";

        /// <summary>警告に載せる、字形の無い文字の数の上限。</summary>
        internal const int MaxReportedMissingGlyphs = 50;

        private const string GaijiResourceName = "gaiji";

        private const string ApplyGaijiScript = @"async (cfg) => {
            const style = document.createElement('style');
            style.textContent =
                ""@font-face{font-family:'HangaGaiji';src:"" + cfg.src + "";unicode-range:"" + cfg.range + "";}"" +
                ""@font-face{font-family:'HangaGaijiIvs';src:"" + cfg.src + "";}"" +
                "".hanga-ivs{font-family:'HangaGaijiIvs' !important;}"";
            (document.head || document.documentElement).appendChild(style);

            // 異体字セレクタ(U+E0100〜U+E01EF)付きの文字を、範囲を絞らない外字用フォントで描く要素に包む(要件6.4)
            const ivs = /[\s\S](?:\uDB40[\uDD00-\uDDEF])/g;
            const walker = document.createTreeWalker(document.body || document.documentElement, NodeFilter.SHOW_TEXT);
            const targets = []; let node;
            while ((node = walker.nextNode())) {
                const parent = node.parentElement;
                if (!parent || ['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE', 'TEXTAREA'].includes(parent.tagName)) continue;
                ivs.lastIndex = 0;
                if (ivs.test(node.nodeValue)) targets.push(node);
            }
            for (const text of targets) {
                const fragment = document.createDocumentFragment(); const s = text.nodeValue; let i = 0, m;
                ivs.lastIndex = 0;
                while ((m = ivs.exec(s))) {
                    fragment.appendChild(document.createTextNode(s.slice(i, m.index)));
                    const span = document.createElement('span'); span.className = 'hanga-ivs'; span.textContent = m[0];
                    fragment.appendChild(span); i = m.index + m[0].length;
                }
                fragment.appendChild(document.createTextNode(s.slice(i)));
                text.parentNode.replaceChild(fragment, text);
            }

            // 全要素のフォント指定の末尾に外字用フォントを足す(本文フォントに字形が無い文字だけが外字用フォントで描かれる。要件6.2)
            for (const el of document.querySelectorAll('*')) {
                const family = getComputedStyle(el).fontFamily;
                if (family && family.indexOf('HangaGaiji') < 0) el.style.setProperty('font-family', family + "", 'HangaGaiji'"", 'important');
            }
            await document.fonts.ready;
            return true;
        }";

        /// <summary>
        /// 字形の無い文字を探す(要件6.7)。判定用フォントA・B(全符号位置に形の違う字形を持つ)を要素のフォント指定の末尾に足して canvas に描き比べ、
        /// 結果が異なれば判定用フォントまで落ちた(= 指定したどのフォントにも字形が無い)とみなす。OS の代替フォントは判定用フォントより後にしか使われないため、判定は OS に左右されない。
        /// </summary>
        private const string FindMissingGlyphsScript = @"async (cfg) => {
            const style = document.createElement('style');
            style.textContent = ""@font-face{font-family:'HangaProbeA';src:url('"" + cfg.probeA + ""');}@font-face{font-family:'HangaProbeB';src:url('"" + cfg.probeB + ""');}"";
            (document.head || document.documentElement).appendChild(style);
            await Promise.all([document.fonts.load('40px HangaProbeA'), document.fonts.load('40px HangaProbeB')]);
            const canvas = document.createElement('canvas'); canvas.width = 64; canvas.height = 64;
            const g = canvas.getContext('2d', { willReadFrequently: true });
            const draw = (font, ch) => { g.clearRect(0, 0, 64, 64); g.font = font; g.fillText(ch, 4, 48); return g.getImageData(0, 0, 64, 64).data; };
            const same = (a, b) => { for (let i = 3; i < a.length; i += 4) if (a[i] !== b[i]) return false; return true; };
            const missing = []; const seen = new Set();
            const walker = document.createTreeWalker(document.body || document.documentElement, NodeFilter.SHOW_TEXT); let node;
            while ((node = walker.nextNode())) {
                const el = node.parentElement;
                if (!el || ['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE'].includes(el.tagName)) continue;
                const cs = getComputedStyle(el);
                if (cs.display === 'none' || cs.visibility === 'hidden') continue;
                const family = cs.fontFamily;
                for (const ch of Array.from(node.nodeValue)) {
                    if (/\s/.test(ch) || /[︀-️]|[\u{E0100}-\u{E01EF}]|[​-‍⁠﻿]/u.test(ch)) continue;
                    const key = family + '\u0000' + ch;
                    if (seen.has(key)) continue; seen.add(key);
                    if (!same(draw('40px ' + family + ', HangaProbeA', ch), draw('40px ' + family + ', HangaProbeB', ch)) && !missing.includes(ch)) missing.push(ch);
                }
            }
            style.remove();
            return missing;
        }";

        private readonly HangaOptions options;
        private readonly IReadOnlyDictionary<string, HangaResource> resources;
        private readonly string? gaijiSource;

        public GlyphSupport(HangaOptions options)
        {
            this.options = options;
            var map = new Dictionary<string, HangaResource>(StringComparer.Ordinal);
            if (options.DetectMissingGlyphs)
            {
                map["probe-a.ttf"] = new HangaResource("font/ttf", ReadEmbedded("probe-a.ttf"));
                map["probe-b.ttf"] = new HangaResource("font/ttf", ReadEmbedded("probe-b.ttf"));
            }

            if (!string.IsNullOrWhiteSpace(options.GaijiFontFamily))
            {
                // インストール済みのフォントを名前で参照する(ファイルを転送しないため速い。要件10.3)
                gaijiSource = "local(" + CssString(options.GaijiFontFamily!) + ")";
            }
            else if (!string.IsNullOrWhiteSpace(options.GaijiFontFile))
            {
                string path = options.GaijiFontFile!;
                string type = path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ? "font/otf" : "font/ttf";
                map[GaijiResourceName] = new HangaResource(type, File.ReadAllBytes(path));
                gaijiSource = "url('/__hanga/" + GaijiResourceName + "')";
            }

            resources = map;
        }

        /// <summary><c>/__hanga/</c> で返すファイル(判定用フォント、外字用フォントのファイル)。</summary>
        public IReadOnlyDictionary<string, HangaResource> Resources => resources;

        /// <summary>外字用フォントの適用と異体字の包み込み(要件6.1〜6.5)。外字用フォントが設定されていなければ何もしない。</summary>
        public async Task ApplyAsync(IPage page)
        {
            if (gaijiSource == null)
            {
                return;
            }

            await page.EvaluateFunctionAsync<bool>(ApplyGaijiScript, new { src = gaijiSource, range = GaijiUnicodeRange }).ConfigureAwait(false);
        }

        /// <summary>字形の無い文字を探し、見つかれば警告にする(要件6.7)。検出が無効なら null。</summary>
        public async Task<HangaWarning?> FindMissingGlyphsAsync(IPage page)
        {
            if (!options.DetectMissingGlyphs)
            {
                return null;
            }

            string[] missing = await page.EvaluateFunctionAsync<string[]>(FindMissingGlyphsScript, new { probeA = "/__hanga/probe-a.ttf", probeB = "/__hanga/probe-b.ttf" }).ConfigureAwait(false);
            if (missing.Length == 0)
            {
                return null;
            }

            string detail = string.Join(" ", missing.Take(MaxReportedMissingGlyphs).Select(Describe))
                + (missing.Length > MaxReportedMissingGlyphs ? $" ほか{missing.Length - MaxReportedMissingGlyphs}文字" : string.Empty);
            return new HangaWarning(HangaWarningKind.MissingGlyph, $"どのフォントにも字形の無い文字が {missing.Length} 文字あります(□で表示されるおそれがあります)。", detail);
        }

        /// <summary>
        /// 外字用フォントがサーバーにあるかを確かめる(要件6.6。名前の指定は Chromium の起動時に確かめる)。
        /// Chromium の起動の直後に <see cref="BrowserHost"/> から呼ぶ。
        /// </summary>
        public async Task VerifyGaijiFontAsync(IBrowser browser)
        {
            if (string.IsNullOrWhiteSpace(options.GaijiFontFamily))
            {
                return;
            }

            IBrowserContext context = await browser.CreateBrowserContextAsync().ConfigureAwait(false);
            try
            {
                IPage page = await context.NewPageAsync().ConfigureAwait(false);
                bool found = await page.EvaluateFunctionAsync<bool>(
                    "src => new FontFace('HangaGaijiCheck', src).load().then(() => true, () => false)", gaijiSource!).ConfigureAwait(false);
                if (!found)
                {
                    throw new HangaConfigurationException(
                        $"外字用フォント '{options.GaijiFontFamily}' がサーバーに見つかりません。フォントをインストールするか、GaijiFontFamily の名前を確かめてください。");
                }
            }
            finally
            {
                await context.CloseAsync().ConfigureAwait(false);
            }
        }

        /// <summary>CSS の文字列(引用符付き)にする。設定値(フォント名)を CSS に入れるため。</summary>
        internal static string CssString(string value)
        {
            var builder = new StringBuilder("'");
            foreach (char c in value)
            {
                if (c == '\'' || c == '\\' || c == '"' || char.IsControl(c) || c == '<' || c == '>')
                {
                    builder.Append('\\').Append(((int)c).ToString("X", CultureInfo.InvariantCulture)).Append(' ');
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.Append('\'').ToString();
        }

        private static string Describe(string ch)
        {
            var codePoints = new List<string>();
            for (int i = 0; i < ch.Length; i += char.IsSurrogatePair(ch, i) ? 2 : 1)
            {
                codePoints.Add("U+" + char.ConvertToUtf32(ch, i).ToString("X4", CultureInfo.InvariantCulture));
            }

            return ch + "(" + string.Join("+", codePoints) + ")";
        }

        private static byte[] ReadEmbedded(string name)
        {
            using Stream stream = typeof(GlyphSupport).Assembly.GetManifestResourceStream("Hanga.Rendering.Fonts." + name)
                ?? throw new InvalidOperationException("埋め込みリソースが見つかりません: " + name);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
}
