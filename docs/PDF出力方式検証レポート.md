# PDF出力方式検証レポート(PuppeteerSharp・テンプレート展開方式)

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
   → **その後、「8」の検証を受けて、ページ内のJavaScriptで仮想オリジンへ移動する方式に変更した**(`Page.navigate` を使わない点は同じ)。
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

## 5. 方向性の選択肢

> **決定(2026年10月3日)**: A案(PuppeteerSharp 18.1.0 に固定)を採用した。`.kiro/steering/tech.md`「② PDF出力」に反映済み。

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

## 6. テンプレート展開方式の検証(RazorLight / ASP.NET Core MVC のビュー描画)

検証日: 2026年10月3日
検証コード: `spikes/pdf-output-verification/razorlight-mvc-view/`(RazorLight)、`spikes/pdf-output-verification/mvc/`(ASP.NET Core MVC)

### 6.1 検証に使ったビュー

利用部門から提示されたテンプレートの例(`Views/Home/Index.cshtml`)をそのまま使った。

```cshtml
@{
    ViewData["Title"] = "Home page";
}

<h1>Hello, World!</h1>
<p>これは MVC ビューの Hello World 画面です。</p>
```

この例は `<html>`/`<head>` を持たないため、`_ViewStart.cshtml`(`Layout = "_Layout"`)と、
`dotnet new mvc` の既定に近い `_Layout.cshtml` を補った。レイアウトは `~/css/site.css`・`~/js/site.js` を `asp-append-version="true"` 付きで読み込み、
フッターにタグヘルパーのリンク(`asp-controller`/`asp-action`)を持つ。`site.js` はDOMに要素を追加する(見た目に関わるJavaScriptの例)。

### 6.2 結果

| 確認項目 | RazorLight 2.3.1 | ASP.NET Core 5 の `IRazorViewEngine` |
|---|---|---|
| 提示されたビューをそのままコンパイルできる | **×**(`The name 'ViewData' does not exist in the current context`) | ○ |
| `_ViewStart.cshtml` によるレイアウトの自動適用 | ×(ビュー側に `Layout` の明示が必要) | ○ |
| `ViewData["Title"]` がレイアウトの `<title>` に入る | ×(`ViewBag` への書き換えが必要) | ○ |
| `~/css/site.css` のパス解決 | ×(`~/` のまま出力) | ○(`/css/site.css`) |
| タグヘルパー(`asp-append-version`、`asp-controller` 等) | ×(属性のまま出力) | ○(`?v=<ハッシュ>` の付与、`href="/Home/Privacy"`) |
| 外部CSS・JavaScriptを反映したPDF(PuppeteerSharp 18.1.0、setcontent 方式) | (未実施) | ○(見出しの色、`site.js` が追加した要素がPDFに出た) |

RazorLight の列の2行目以降は、`ViewData` を `ViewBag` に置き換え、レイアウトを明示して確認した。
ASP.NET Core の列は、自己完結型で発行した ASP.NET Core 5.0.17 上で確認した。

### 6.3 わかったこと・設計への反映

- **RazorLightは、MVCのビューとして書かれたテンプレートを扱えない。** 帳票ごとにRazorLight向けの書き方(`ViewBag`、レイアウトの明示、`~/` やタグヘルパーを使わない)を強いることになる。
- **ASP.NET Core MVC のビュー描画機能なら、アプリの画面と同じ書き方のビューをそのままHTMLにできる。** RazorLightへの依存(2023年1月以降更新が止まっている)も不要になる。
- コントローラーの外からビューを描画するには、`HttpContext`(`RequestServices` を含む)、`RouteData`(`controller` の値でビューの探索場所が決まる)、
  `ITempDataProvider` が必要だった。HTTPリクエストの処理中でない場所(バックグラウンド処理など)から呼ぶ場合の作り方は、設計書で決める。
- タグヘルパーが出力するURLは、アプリのパスベース(IISの仮想ディレクトリ配下に置いた場合の `/アプリ名` など)を含む。
  リソース要求をフォルダに対応付けるときは、パスベースを取り除く必要がある(設計書で扱う)。
- `asp-append-version` が付けるクエリ文字列(`?v=...`)は、フォルダへの対応付けでは無視してよい(検証コードでも無視した)。
- 本番環境は IIS の仮想ディレクトリを使わず、IIS から Kestrel へポートをリバースプロキシする構成である(利用部門の回答)。このため、パスベースの問題は現行の構成では生じない。
  将来の構成変更に備え、パスベースを取り除く処理は設計に含める。
- 用紙サイズを `Width = "210mm"`・`Height = "297mm"` で指定すると、PDFのページは 595.92 × 841.92pt になった(`PaperFormat.A4` の 18.1.0 での値 595.92 × 842.88pt とは異なる)。
  Chromiumの内部で丸めが入るため、mm指定でも正確なA4(595.28 × 841.89pt)にはならない。指定方法と許容範囲は設計書で決める。

### 6.4 バッチ(HTTPリクエストの外)での生成

利用部門の想定する使い方は次の2つである。

1. **画面のPDF化**: コントローラーにPDF用のアクションを追加し、オペレーターがそのアクションを呼ぶと、画面と同じビューをPDFにしたものを受け取る(ブラウザのPDF印刷が使えないための代用)。
2. **バッチでの一括生成**(将来の候補): 顧客向け帳票などを、ビューからまとめてPDFにする。

1はHTTPリクエストの処理中に呼ぶため、6.2の検証そのものである。2について、Webサーバーを起動せずにビューからPDFを作れるかを確認した
(`spikes/pdf-output-verification/mvc/` を `<chromeの実行ファイル> batch <出力フォルダ>` の引数で実行する)。

- **結果**: ASP.NET Core 5.0.17 上で、ポートを開かずに同じビューから3件のPDFを連続して生成できた。出力されたHTMLは、画面(HTTPリクエスト)の場合と同一だった
  (レイアウト、`ViewData`、`~/` の解決、`asp-append-version`、タグヘルパーによるリンク、CSS、JavaScriptがすべて反映された)。
- **必要だった準備**:
  - `HttpContext` は `DefaultHttpContext` を作り、`RequestServices` に依存性注入のスコープを設定した。
  - タグヘルパーによるリンクの生成(`asp-controller`/`asp-action`)には、アプリのルーティング情報が必要だった。
    準備しないと `Could not find an IRouter associated with the ActionContext` で失敗した。
    ポートを開かない「何もしないサーバー」(`IServer` の実装)に差し替えてホストを起動し、ルーティングの登録だけを行わせたうえで、
    `HttpContext` にダミーのエンドポイントを設定したところ、画面の場合と同じリンクが生成された。
- **制約(設計で扱う)**:
  - ビューが、リクエストやログインユーザーに依存する情報(`User`、`Request` のクエリ文字列、セッション、Cookie など)を使っている場合、バッチではそれらの値が無い。
    バッチで使う帳票のビューは、表示に必要な値をモデルから受け取る作りにする必要がある。
  - 今回はWebアプリと同じ実行ファイルをバッチの引数で動かした。バッチを別の実行ファイルにする場合は、ビューを共有する方法(Razorクラスライブラリにまとめる等)が必要になる。
  - 今回は帳票ごとにChromiumを起動した。大量に生成する場合は、Chromiumの起動を使い回すことを設計で検討する。

## 7. 外字・異体字の検証

検証日: 2026年10月3日
検証コード: `spikes/pdf-output-verification/console/`(環境変数 `HANGA_GAIJI_FONT`・`HANGA_GAIJI_SRC` で外字用フォントの注入を有効にする)

利用部門から外字への対応を求められたため、帳票の本文フォント(IPAゴシック)に字形の無い「𠮷」(U+20BB7。CJK統合漢字拡張B)と、
異体字セレクタ付きの文字(「葛」+U+E0100、「辻」+U+E0100)を、外字用フォント IPAmj明朝 で描けるかを確認した。PuppeteerSharp 18.1.0、Chrome 141。

| 方式 | 内容 | 「𠮷」 | 異体字 | ページの準備にかかった時間 |
|---|---|---|---|---|
| 何もしない(IPAmj明朝はサーバーに未インストール) | — | ×(□) | ×(本文フォントの通常の字形) | 約4秒 |
| 何もしない(IPAmj明朝をサーバーにインストール) | Chromium が自動で選ぶ代替フォントに任せる | ○ | (未確認) | 約4秒 |
| Hangaが外字用フォントを注入(ファイルを転送) | `@font-face` でフォントファイルを渡し、全要素のフォント指定の末尾に外字用フォントを足す | ○ | ×→○(下記の処理を追加後) | **約9.5秒** |
| Hangaが外字用フォントを注入(インストール済みのフォントを名前で参照) | 上と同じだが、`src: local('IPAmj明朝')` でサーバーのフォントを参照する | ○ | ○(下記の処理を追加後) | **約1.9秒** |

- **自動の代替フォントに任せる方式は、OSに依存する。** この環境(Linux)では fontconfig がIPAmj明朝を選んだが、
  Windows の Chromium は代替フォントの選び方が異なり、同じ結果になる保証がない。このため、Hangaが外字用フォントを明示的に注入する方式をとる。
- **フォントファイルを転送する方式は遅い。** IPAmj明朝は46MBあり、リソース要求への介入でChromiumに渡すのに約5秒余分にかかった。
  **サーバーにインストールしたフォントを名前で参照する方式(`local()`)なら、外字対応をしない場合と同等以下の時間で済んだ。**
- `unicode-range` で外字用フォントを使う範囲を、通常のフォントに無いことが多い範囲(CJK統合漢字拡張B以降、私用領域)に絞った。
  該当する文字が無いページでは、外字用フォントが読み込まれない。
- **異体字は、外字用フォントを末尾に足すだけでは反映されない。** 本文フォントに基底の文字(「葛」など)の字形があるため、Chromium は本文フォントで描き、
  異体字セレクタを無視する。異体字セレクタ付きの文字を見つけて外字用フォントで描く `<span>` に包む処理(JavaScript)を注入したところ、外字用フォントで描かれるようになった。
- 外字用フォントの字形は明朝体のため、ゴシック体の本文の中では、その文字だけ書体が変わる。

**未確認**: 異体字の字形が、指定どおりの異体字になっているかは確認できていない。IPAmj明朝を直接指定した表示でも、
今回使った「葛」+U+E0100 と「葛」が同じ字形だったため、比較にならなかった。IPAmj明朝の文字情報一覧で異体字の字形が異なる組み合わせを選んで確認する。

## 8. サーバーへの非同期の値の取得

検証日: 2026年10月3日
検証コード: `spikes/pdf-output-verification/inproc/`

利用部門の回答: ビューのJavaScriptが、相対パスのAPIへ非同期で値を取りに行く可能性がある。認証はCookie認証。

### 8.1 検証の構成

- ASP.NET Core 5.0.17 のアプリに、Cookie認証のログイン、ログインが必要なAPI(`/api/orders/{id}`)、
  表示時に `fetch('/api/orders/123')` で値を取得して描画するビュー(`Views/Home/Order.cshtml` と `wwwroot/js/order.js`)を用意した。
- ログインが必要なPDF用のエンドポイント(`/order/pdf`)が、ビューをHTMLにし、PuppeteerSharp 18.1.0 でPDFにして返す。
- 仮想オリジン(`https://hanga.invalid/`)への要求は、すべてアプリ自身のミドルウェアのパイプラインにプロセス内で渡した。
  パイプラインは `IStartupFilter` で捕まえ、要求ごとに `DefaultHttpContext` を作り、オペレーターの要求の `Cookie` ヘッダーを付けた。

### 8.2 結果

| 段階 | 結果 |
|---|---|
| 未ログインで `/order/pdf` | 302(ログイン画面へ)。PDF用アクション自体が認証で守られる |
| ログイン済みで `/order/pdf`、HTMLを `SetContentAsync` で流し込む方式 | **×**。APIはアプリ内でオペレーターの権限で応答した(200)が、ブラウザ側の `fetch` が `Failed to fetch` で失敗した |
| ログイン済みで `/order/pdf`、ページ内のJavaScriptで仮想オリジンへ移動する方式 | **○**。APIの値(注文番号・顧客・合計)がPDFに入った。Chrome 141・Chrome 154 の両方で確認 |

- `SetContentAsync` 方式が失敗したのは、ページのオリジンが空白のページ(`about:blank`)のままで、`<base>` で向けた仮想オリジンへの `fetch` が
  別オリジンへの要求とみなされ、CORS の制限を受けたため。
- ページ内で `location.href = 'https://hanga.invalid/__hanga/report'` を実行して移動し、その要求に帳票のHTMLを返すと、ページのオリジンが仮想オリジンになり、
  相対パスの `fetch` は同一オリジンの要求になった。CDPの `Page.navigate` 命令を使わないため、PuppeteerSharp 18.1.0 の `Invalid referrerPolicy` の問題も起きない。
- 静的ファイル(`asp-append-version` のクエリ文字列付き)も、アプリの `UseStaticFiles` がそのまま応答した。フォルダへの対応付けを自前で持つ必要がなくなる。
- ブラウザが自動で `favicon.ico` を要求し、アプリが404を返した。Hangaが応えて、失敗として扱わないようにする。

### 8.3 設計への反映

- 外部リソースの解決は「フォルダからファイルを返す」方式をやめ、**仮想オリジンへの要求をすべてアプリのパイプラインにプロセス内で渡す**方式にする(`.kiro/steering/tech.md`「外部リソースとサーバーへの要求の扱い」)。
- ページのオリジンが仮想オリジンになるため、「2.2」で必要だったWebフォントの `Access-Control-Allow-Origin` も不要になる。
- 要求ごとに作る `HttpContext` に何を引き継ぐか(`Cookie` 以外のヘッダー、`Host`、言語設定など)と、アプリのミドルウェアが仮の要求に対して想定外の動きをしないか
  (HTTPSへのリダイレクト、Antiforgery、ログ出力など)は、設計時に確認する。

### 8.4 同時実行(スレッドセーフ)

検証日: 2026年10月4日。検証コード: `spikes/pdf-output-verification/inproc/concurrent-test.sh`

8人のオペレーター(user1〜user8)がそれぞれログインし、同時に `/order/pdf` を要求した。検証コードは帳票ごとにChromiumを起動する作り。

- 8件とも200で返り、**各PDFには本人の権限でAPIから取得した値(「userN が取得」)が入っていた。取り違えは無かった。**
- 1件あたり約10秒かかった(8つのChromiumが同時に起動したため。1件だけの場合は数秒)。
- 各要求をアプリのパイプラインに渡す処理は、要求ごとに `HttpContext` と依存性注入のスコープを作っており、ASP.NET Core が通常の同時リクエストを処理するのと同じ形になる。

## 9. 未検証の事項

この開発環境はLinuxのため、次の点は Windows Server の検証環境で確認する必要がある。検証コードはそのまま使える(`spikes/pdf-output-verification/README.md`)。

- **Visual Studio 2019 の実機でのビルド**(MSBuild 16.11 / NuGet 5.11 での復元を含む)。今回はコンパイラをRoslyn 3.11に差し替えただけで、MSBuildとNuGetは新しいものを使った。
- **Windows上の .NET 5 ランタイムでの動作**。
- **IISのアプリケーションプールのユーザーからのChromium起動**。検証コードは root で動かすため `--no-sandbox` を付けている。Windowsではサンドボックスを有効にしたまま起動できるかを確認する。
- 本番に置くChromium(Google Chrome / Chrome for Testing / Microsoft Edge 等)での動作。
- 実際の帳票テンプレート(呼び出し元アプリの既存ビュー)とレイアウトでの動作。今回は提示された例と、既定の形に近いレイアウトで確認した。
- Chromiumを使い回す方式での同時実行(8.4はリクエストごとにChromiumを起動する方式で確認した)と、多数の同時要求での性能・安定性。
- Windows でのインストール済みフォントの名前での参照(`local('IPAmj明朝')`)。
- 異体字が指定どおりの字形で描かれるか(「7. 外字・異体字の検証」)。
- 実際の画面のAPI呼び出し(`fetch` か `XMLHttpRequest` か jQuery か、POSTを使うか、Antiforgery のトークンを使うか)での動作。
