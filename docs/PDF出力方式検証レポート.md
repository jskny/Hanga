# PDF出力方式検証レポート(PuppeteerSharp + RazorLight)

検証日: 2026年10月3日
検証環境: Claude Code on the web(Ubuntu 24.04、Linux)。Windows Server での確認は行っていない(「未検証の事項」参照)
検証コード: `spikes/pdf-output-verification/`(実行方法は同ディレクトリの `README.md`)

## 1. 結論

1. **PuppeteerSharp + RazorLight の構成で、Hangaに必要な処理は実現できる。**
   CSHTML → HTML → PDF の流れで、外部CSS・JavaScript・Webフォント・画像を反映したPDFを生成でき、
   許可していない外部への通信は遮断できた。
2. **ブラウザだけが新しくなると動かなくなる現象は、実際に再現した。**
   ただし、Hangaが使う命令を絞る(ページを開く命令 `Page.navigate` を使わず、HTMLを直接流し込む)ことで、
   2021年〜2026年の7つの版のPuppeteerSharpがすべて Chrome 141 と Chrome 154(検証時点で入手できた最も新しい版)で動いた。
3. **.NET 5 / Visual Studio 2019 については、PuppeteerSharp 20以降に注意が必要。**
   どの版も VS2019 と同じコンパイラ(Roslyn 3.11)でビルドでき、本物の .NET 5 ランタイム(5.0.17)でも動いた。
   ただし **PuppeteerSharp 20以降は、.NET 5 を公式にはサポートしない依存パッケージ(`System.Text.Json` 8〜10、`Microsoft.Extensions.*` 8.0 など)を連れてくる**。
   ASP.NET Core 5 アプリに組み込むと、アプリ全体の `Microsoft.Extensions.Logging` と `System.Text.Json` がそれらの版に置き換わる。
   **PuppeteerSharp 18.1.0 以下には、この問題はない**(詳細は「4. .NET 5 / Visual Studio 2019 への対応状況」)。

## 2. 検証した内容

### 2.1 検証用の帳票

`spikes/pdf-output-verification/console/` の請求書テンプレート(`templates/invoice.cshtml`)。

- RazorLight でモデル(宛先・請求番号・明細60行)を埋め込む。`@foreach` による繰り返し、HTMLエスケープ。
- `<link>` で外部CSS、`<script>` で外部JavaScript、CSSの `@font-face` でWebフォント(IPAゴシック)、`<img>` でSVG画像を読み込む。
- JavaScriptが金額を3桁区切りの「¥1,234」形式に整形し、合計を計算して描画する(見た目に関わるJavaScriptの例)。
- 許可していない外部(`https://cdn.example.com/blocked.js`)を読み込む `<script>` を1つ含める。
- A4、余白指定、フッターにページ番号(「1 / 2」)、表の見出し行を各ページで繰り返す(`thead { display: table-header-group; }`)。

### 2.2 外部リソースの扱い

Chromiumからのリソース要求に介入し(`SetRequestInterceptionAsync` と `Request` イベント)、次のように応答した。

- 仮想のオリジン `https://hanga.invalid/` 配下の要求は、URLのパスを `wwwroot` フォルダに対応付け、ファイルを直接返す。HTTP通信は発生しない。
- `wwwroot` の外を指すパス(`../` 等)と、存在しないファイルには 404 を返し、問題として記録する。
- それ以外のオリジンへの要求は遮断し、問題として記録する。

HTMLの渡し方は2通り試した。

| 方式 | 内容 |
|---|---|
| navigate | 仮想オリジンのURL(`https://hanga.invalid/__report`)を `GoToAsync` で開き、その要求に展開済みのHTMLを返す。内部では `Page.navigate` 命令を使う |
| setcontent | 展開済みのHTMLの `<head>` 直後に `<base href="https://hanga.invalid/">` を挿入し、`SetContentAsync` で流し込む。`Page.navigate` 命令を使わない |

setcontent 方式ではページのオリジンと仮想オリジンが異なるため、Webフォントの応答に `Access-Control-Allow-Origin: *` を付ける必要があった。

### 2.3 結果(PuppeteerSharp 25.12.0 / Chrome 141)

- 外部CSS・JavaScript・Webフォント・SVG画像がすべて反映された(`document.fonts.check` でWebフォントの読み込みを確認)。
- JavaScriptによる整形・合計の描画が反映された(合計 ¥8,967,408)。
- `https://cdn.example.com/blocked.js` は遮断され、問題として記録された。navigate 方式では `favicon.ico` の自動要求も404として記録された。
- 2ページのA4 PDF。見出し行の繰り返し、ページ番号が反映された。
- `pdftotext` で全60行の文字列を取り出せた(文字列の検索・コピーができる)。
- **気付いた点**:
  - 「𠮷」(U+20BB7)は豆腐(□)になった。IPAゴシックにこの字形が無いため。外字を扱う場合は、IPAmj明朝などをフォントの候補に加える必要がある(Utsushiと同じ課題)。
  - 太字(`<h1>`、`<th>`)は `pdffonts` で `Type 3` と表示された。IPAゴシックに太字の字形が無く、Chromiumが太字を合成したためとみられる。
    `pdftotext` で太字の部分の文字も取り出せており、検索・コピーへの影響は見られなかった。太字の字形を持つフォントを使えば解消する見込み(未確認)。

処理時間の目安(この開発環境): RazorLight の初回コンパイル 約2.5秒、Chromium起動 約0.5〜1.5秒、ページの読み込み完了まで 約1〜4秒、PDF生成 約0.2〜0.8秒。
初回コンパイルとChromium起動は、テンプレートのキャッシュやブラウザの使い回しで2回目以降を短縮できる(設計時に検討する)。

## 3. ライブラリとブラウザの版のずれへの耐性

運用部門がChromiumを上げ、PuppeteerSharpは古いまま、という状況を再現するため、2021年〜2026年のPuppeteerSharpで次の3種類のブラウザを操作した。

- **Chrome 141**: この開発環境にプリインストールされているChromium(141.0.7390.37)
- **Chrome 154**: Chrome for Testing 154.0.8037.57(通常のChrome)。PuppeteerSharp 25.12.0 が標準で想定する版で、検証時点で入手できた最も新しい版
- **headless shell 154**: Chrome for Testing の chrome-headless-shell 154.0.8037.57(ヘッドレス専用の軽量版)

| PuppeteerSharp | 公開日(NuGet) | 標準で想定するChrome | Chrome 141 navigate | Chrome 141 setcontent | Chrome 154 navigate | Chrome 154 setcontent | headless shell 154 navigate | headless shell 154 setcontent |
|---|---|---|---|---|---|---|---|---|
| 25.12.0 | 2026-09 | 154 | ○ | ○ | ○ | ○ | ○ | ○ |
| 20.2.6 | 2026-01 | 138 | ○ | ○ | ○ | ○ | ○ | ○ |
| 18.1.0 | 2024-08 | 127 | **×** | ○ | **×** | ○ | **×** | ○ |
| 15.1.0 | 2024-03 | 123 | **×** | ○ | **×** | ○ | **×** | ○ |
| 10.1.4 | 2023-08 | 約112(revision 1108766) | ○ | ○ | ○ | ○ | ○ | ○ |
| 7.1.0 | 2022-07 | 約101(revision 970485) | ○ | ○ | ○ | ○ | ○ | ○ |
| 5.1.0 | 2021-09 | 約92(revision 884014) | ○ | ○ | ○ | ○ | ○ | ○ |

公開日はNuGetの登録情報の日付(再公開された版は、元のリリースより新しい日付になっている可能性がある)。
「標準で想定するChrome」は各版の `PuppeteerSharp.dll` に埋め込まれた値。10.1.4以前はChromiumのrevision番号で、Chromeの版は概算。
○はPDFを生成でき、ページ数2・全60行の文字列を取り出せたもの。

- 15.1.0 と 18.1.0 の navigate 方式は、どのブラウザでも `Protocol error (Page.navigate): Invalid referrerPolicy` で失敗した。
  これらの版は、新しいChromeが受け付けない値をページを開く命令に付けて送っている。**ブラウザだけが上がって動かなくなる現象の実例**である。
  それより古い 5.1.0〜10.1.4 は navigate 方式でも動いており、「古い版ほど壊れやすい」わけではない。壊れるかどうかは、その版がどの命令にどんな値を送るかによる。
- setcontent 方式は、21通り(7つの版 × 3種類のブラウザ)すべてで成功した。
- **ブラウザを上げたときの見た目の変化**: PuppeteerSharp 18.1.0・25.12.0 とも、Chrome 141 と Chrome 154 の出力を画像化すると、この帳票ではピクセル単位で一致し、取り出した文字列も一致した。
  headless shell 154 の出力は、通常のChromeとわずかに異なった(本番で使うブラウザの種類を途中で変えない方がよい)。

**この結果から言えること**: 古いPuppeteerSharpでも、使う命令を絞れば、少なくとも Chrome 154 までは問題なく動く。
ただし、将来のChromeが setcontent 方式で使う命令(HTMLの流し込み、リソース要求への介入、PDFへの印刷)の仕様を変えた場合は動かなくなりうる。これは最新版のPuppeteerSharpでも、修正版が出るまでは同じである。

### ライブラリを上げると出力が変わる例

同じブラウザでも、PuppeteerSharp の版によって出力が変わった。

- **用紙サイズ**: `PaperFormat.A4` の高さが、18.1.0以前は842.88pt、20.2.6以降は841.92pt(正確なA4は841.89pt)。このため画像化した結果が一致しない。
- **タグ付きPDF**: 15.1.0だけ、PDFに文書構造の情報(`StructTreeRoot`)が付かず、ファイルサイズが約半分になった。見た目は18.1.0と一致した。
- 25.12.0では、`SetContentAsync(string, NavigationOptions)` が非推奨(コンパイル時に警告CS0618)になっている。今後の版で使えなくなる可能性がある。

### Hangaの設計への反映(提案)

1. **HTMLは setcontent 方式で渡す**(`Page.navigate` を使わない)。外部リソースは `<base>` と仮想オリジン、リソース要求への介入で解決する。
2. **用紙サイズはライブラリの定数(`PaperFormat.A4`)に頼らず、mm単位の数値(または CSS の `@page { size: A4 }` と `PreferCSSPageSize`)で明示する。**
3. **PuppeteerSharpの版を上げるときも、ブラウザを上げるときと同じ検証手順(ページ数・文字列・画像化した見た目の比較)を通す。**

## 4. .NET 5 / Visual Studio 2019 への対応状況

### 4.1 確認したこと

| 確認項目 | 25.12.0 | 20.2.6 | 18.1.0 | 15.1.0 | 10.1.4 | 7.1.0 | 5.1.0 |
|---|---|---|---|---|---|---|---|
| `net5.0` 向けにビルドできる(.NET 10 SDK) | ○ | ○ | ○ | ○ | ○ | ○ | ○ |
| VS2019 と同じコンパイラ(Roslyn 3.11)でビルドできる | ○ | ○ | ○ | 未 | ○ | 未 | 未 |
| 本物の .NET 5 ランタイム(5.0.17)で動く(コンソール) | ○ | ○ | ○ | 未 | 未 | 未 | 未 |
| ASP.NET Core 5.0.17 アプリの中で動く | ○ | 未 | ○ | 未 | 未 | 未 | 未 |
| **.NET 5 を公式にサポートしない依存パッケージ** | **14個** | **9個** | なし | なし | なし | なし | なし |

(「未」は今回試していないもの)

- Roslyn 3.11 でのビルドは、Utsushiと同じく `Microsoft.Net.Compilers.Toolset` 3.11.0 に差し替えて行った(ログで `microsoft.net.compilers.toolset/3.11.0/.../csc.dll` が使われたことを確認)。RazorLight 2.3.1 も含めてビルドできた。
- .NET 5 ランタイムは、自己完結型の発行でNuGetから取得した `Microsoft.NETCore.App.Runtime.linux-x64` 5.0.17 / `Microsoft.AspNetCore.App.Runtime.linux-x64` 5.0.17 を使った。

### 4.2 PuppeteerSharp 20以降の依存パッケージの問題

PuppeteerSharp 20以降は netstandard2.0 向けのビルドを提供し続けているが、依存先がより新しい .NET 向けのパッケージになっている。
ビルド時に「`<パッケージ> doesn't support net5.0 and has not been tested with it`」という警告が出る(警告であり、ビルドは通る)。

- 20.2.6(9個): `Microsoft.Extensions.DependencyInjection(.Abstractions)` / `Logging(.Abstractions)` / `Options` / `Primitives` 8.0.0、`System.Diagnostics.DiagnosticSource` 8.0.0、`System.Text.Encodings.Web` 8.0.0、`System.Text.Json` 8.0.5
- 25.12.0(14個): 上記の `Microsoft.Extensions.*` 8.0.0 に加え、`System.Text.Json` / `System.Text.Encodings.Web` / `System.IO.Pipelines` / `System.Threading.Channels` / `Microsoft.Bcl.Memory` / `Microsoft.Bcl.TimeProvider` 10.0.12、`System.Runtime.CompilerServices.Unsafe` 6.1.2、`System.Diagnostics.DiagnosticSource` 8.0.0
- 18.1.0以下: 依存は `Newtonsoft.Json` 13.0.1、`Microsoft.Extensions.Logging` 2.0.2、`Microsoft.Bcl.AsyncInterfaces` 1.1.0 で、警告は出ない。

**影響はHangaだけでなく、呼び出し元のアプリ全体に及ぶ。**
ASP.NET Core 5.0.17 のアプリに PuppeteerSharp 25.12.0 を組み込んで実行したところ、アプリ内で読み込まれた版は次のとおりだった。

| | PuppeteerSharp 25.12.0 を組み込んだ場合 | 18.1.0 を組み込んだ場合 |
|---|---|---|
| `Microsoft.Extensions.Logging` | **8.0.0.0**(ASP.NET Core 5 本体の5.0を置き換え) | 5.0.0.0 |
| `System.Text.Json` | **10.0.0.0**(.NET 5 本体の5.0を置き換え) | 5.0.0.0 |

今回の最小構成のアプリ(ログ出力、静的ファイル、ルーティング、PDF生成)では、どちらも問題なく動いた。
しかし、これはMicrosoftがテストしていない組み合わせであり、呼び出し元アプリの他の機能(JSONのシリアライズ、ログ、DI、他のライブラリ)で挙動が変わる可能性がある。

### 4.3 RazorLight の注意点

- RazorLight 2.3.1 は `net5.0` 向けのビルドを持ち、依存も 5.0.0 系でそろっている。上記の問題はない。
- RazorLightを参照すると、アプリが ASP.NET Core の共有フレームワーク(`Microsoft.AspNetCore.App`)を前提にする(`runtimeconfig.json` で確認)。呼び出し元はASP.NET Coreアプリなので問題にならない。
- **発行したWebアプリでは、呼び出し元のプロジェクトに `<PreserveCompilationReferences>true</PreserveCompilationReferences>` が必要。**
  無いと、テンプレートのコンパイル時に `Cannot find reference assembly 'Microsoft.AspNetCore.Antiforgery.dll' ...` で失敗した。設定を加えたら動いた。
  Hangaの利用手順(`docs/ライブラリの使い方.md` を作るとき)に必ず書く。
- 最終リリースは2023年1月で、更新が止まっている(`.kiro/steering/tech.md`)。

## 5. 方向性の選択肢(要判断)

PuppeteerSharpの版について、次の選択肢がある。

| 案 | 内容 | 利点 | 懸念 |
|---|---|---|---|
| A. 18.1.0 に固定 | .NET 5 の依存の問題がない最後の版を使う | 呼び出し元アプリの依存を変えない。今回の検証で Chrome 141・154 での動作を確認済み(setcontent 方式) | 2024年8月の版で、PuppeteerSharp側の修正はもう入らない。今後のChromeで動かなくなった場合、自分たちで直す必要がある(下記C) |
| B. 最新版(20以降)を使う | 依存の警告を許容する(`SuppressTfmSupportBuildWarnings`) | PuppeteerSharp側の修正を受けられる | 呼び出し元アプリの `System.Text.Json` / `Microsoft.Extensions.*` を置き換える。サポート外の組み合わせ |
| C. PuppeteerSharpを改修して使う | 18.1.0などをフォーク(MIT)し、必要な修正を自分たちで当てる | 依存を制御でき、Chromeの変更にも自分たちで追随できる | フォークの保守が必要。改修の範囲は、Hangaが使う命令(起動・HTMLの流し込み・リソース要求への介入・PDF印刷)に限れば小さくできる見込み |
| D. 必要な部分だけを自作する | PuppeteerSharpを使わず、Chrome DevTools Protocol を直接扱う最小限のクライアントを書く | 依存がほぼ無くなる。使う命令が少ないため、実装量は限定的 | 実装・保守の負担。Chromeの起動や終了処理など、細かな落とし穴を自分で扱う必要がある |

**推奨**: 当面は **A(18.1.0固定)** で開発を進め、Hangaの内部でPuppeteerSharpを直接触る範囲を最小限にしておく。
将来Chromeの更新で動かなくなった場合に、C(フォークして修正)またはD(自作)へ切り替えられるようにするためである。
B は、呼び出し元アプリ全体の依存を変えるため、呼び出し元の開発チームと合意できない限り採らない。

## 6. 未検証の事項

この開発環境はLinuxのため、次の点は Windows Server の検証環境で確認する必要がある。検証コードはそのまま使える(`spikes/pdf-output-verification/README.md`)。

- **Visual Studio 2019 の実機でのビルド**(MSBuild 16.11 / NuGet 5.11 での復元を含む)。今回はコンパイラをRoslyn 3.11に差し替えただけで、MSBuildとNuGetは新しいものを使った。
- **Windows上の .NET 5 ランタイムでの動作**。
- **IISのアプリケーションプールのユーザーからのChromium起動**。検証コードは root で動かすため `--no-sandbox` を付けている。Windowsではサンドボックスを有効にしたまま起動できるかを確認する。
- 本番に置くChromium(Google Chrome / Chrome for Testing / Microsoft Edge 等)での動作。
- 実際の帳票テンプレート(呼び出し元アプリの既存ビュー)での動作。特に、`~/` で始まるパスやタグヘルパーなど、RazorLightが対応しないMVCの機能を使っていないか。
- 同時に複数の帳票を生成した場合の性能と安定性。
