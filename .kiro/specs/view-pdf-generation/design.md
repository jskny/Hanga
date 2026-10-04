# 設計書: ASP.NET Core MVC のビューからのPDF生成

対応する要件: `requirements.md`(要件1〜12)。方針は `.kiro/steering/`、方式の根拠は `docs/PDF出力方式検証レポート.md` を参照。

## 概要

呼び出し元アプリのPDF用アクションから、簡易API `Cshtml2Pdf`(仮称)を作って呼ぶ。Hangaは次の順に処理する。

```
PDF用アクション(元の要求)
  │ ① 元の要求から必要な値を写し取る(Cookie・Host・スキーム等)        … 要件3.2, 9.3
  │ ② ビューをHTMLにする(IRazorViewEngine)                           … 要件1
  ▼
共有の変換器(HangaPdfConverter。スレッドセーフ)
  │ ③ 同時実行数の枠を取る(SemaphoreSlim)                             … 要件9.4
  │ ④ 共有のChromiumから、帳票1件用のブラウザコンテキストを作る       … 要件9.2, 10.1
  │ ⑤ ページ内のJavaScriptで仮想オリジンへ移動し、帳票のHTMLを返す    … 要件2.3, 2.4
  │ ⑥ 仮想オリジンへの要求を、アプリのパイプラインにプロセス内で渡す  … 要件3
  │ ⑦ 表示の完了を待つ(ネットワークの静止・完了条件・フォント)        … 要件4
  │ ⑧ 外字用フォントの適用・異体字の包み込み・字形の無い文字の検出    … 要件6
  │ ⑨ 体裁(用紙・倍率・幅の収め・1ページ化・CSSの種類)を決めてPDF化  … 要件5
  │ ⑩ ブラウザコンテキストを破棄し、枠を返す
  ▼
生成結果(PDFのバイト列 + 警告の一覧) → バイト列 / ストリーム / ファイル / IActionResult  … 要件7, 8.6
```

## プロジェクト構成とレイヤー

`.kiro/steering/structure.md` のレイヤー(`Core → Templating → Rendering → Hanga`)に次のとおり割り当てる。

| プロジェクト | 責務 | 主な依存 |
|---|---|---|
| `Hanga.Core` | 例外階層、警告、用紙サイズ・余白などの値型、オプションの型 | なし |
| `Hanga.Templating` | ビューをHTMLにする(`ViewHtmlRenderer`) | ASP.NET Core MVC(共有フレームワーク `Microsoft.AspNetCore.App`) |
| `Hanga.Rendering` | Chromiumの管理、ページの表示、要求の振り分け、注入するスクリプト、PDF化 | PuppeteerSharp 18.1.0 |
| `Hanga` | 公開API(`Cshtml2Pdf`・`HangaPdfConverter`・`AddHanga`)、パイプラインの捕捉とプロセス内の要求の転送 | 上記すべて、ASP.NET Core |

- `Hanga.Rendering` はASP.NET Coreに依存しない。仮想オリジンへの要求は、`Hanga.Rendering` が定義するインターフェース `IVirtualOriginHandler` に渡し、
  その実装(パイプラインへの転送)は `Hanga` が持つ。バッチ(要件1.6)では、転送先のパイプラインを、Hangaがバッチの中に作ったものにする(`.kiro/specs/batch-pdf-generation/design.md`)。
- PuppeteerSharp の型は `Hanga.Rendering` の外に出さない(要件2.6、`tech.md`「Chromiumのバージョンアップへの備え」)。

## 公開API

### 登録(`Startup.ConfigureServices`)

```csharp
services.AddHanga(options =>
{
    options.ChromiumExecutablePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    options.GaijiFontFamily = "IPAmj明朝";            // 外字用フォント(インストール済みのフォント名)
    options.MaxConcurrentRenders = 4;
    options.Strict = false;                           // 既定。true で警告の対象をエラーにする
    options.AllowedExternalHosts.Add("cdn.example.com");
});
```

設定ファイル(`appsettings.json`)からも指定できる。運用部門が、プログラムを修正・再ビルドせずに値を変えられるようにするため。

```csharp
services.AddHanga(Configuration.GetSection("Hanga"));                   // 設定ファイルの値だけを使う
services.AddHanga(Configuration.GetSection("Hanga"), options => { ... }); // 設定ファイルの値を読んだ後、コードで上書きする
```

```json
{
  "Hanga": {
    "ChromiumExecutablePath": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
    "GaijiFontFamily": "IPAmj明朝",
    "MaxConcurrentRenders": 4,
    "RenderTimeout": "00:00:30",
    "AllowedExternalHosts": [ "cdn.example.com" ]
  }
}
```

- 設定ファイルに書かなかった項目は既定値になる(下表)。
- 値は `AddHanga` の時点(登録時)に検証し、範囲外(`MaxConcurrentRenders` が1未満など)なら `HangaConfigurationException` にする。
- 設定ファイルの値を変えた場合は、アプリの再起動で反映する(同時実行数の枠やChromiumの起動引数は起動時に決まるため。動作中の変更は扱わない)。

`AddHanga` は次を登録する(要件12.5)。

- `HangaPdfConverter`(シングルトン。共有の変換器)
- `IStartupFilter`(パイプラインの捕捉。`Startup.Configure` の変更は不要)
- `IHostedService`(`options.LaunchOnStartup = true` なら起動時にChromiumを起動する。要件10.2)。Chromiumの終了は、Webサーバーが処理中の要求を終えた後
  (`IHostApplicationLifetime.ApplicationStopped`)に行う。常駐サービスの停止はWebサーバーの停止より先に呼ばれるため、そこで終了すると処理中のPDFの要求が失敗する(コードレビューの指摘で修正)

### PDF用アクション

```csharp
public async Task<IActionResult> OrderPdf(int id)
{
    var model = await _orders.FindAsync(id);
    var pdf = new Cshtml2Pdf(this, "Order", model);      // 帳票1件ごとに作る。コントローラー名は this から取る(要件1.3)
    pdf.Options.Orientation = PageOrientation.Landscape;  // 既定は A4 縦(要件5.1)
    return await pdf.ToActionResultAsync("注文明細.pdf", PdfDisposition.Inline);  // 要件7
}
```

| メンバー | 内容 |
|---|---|
| `Cshtml2Pdf(Controller controller, string viewName, object? model)` | コントローラーの `HttpContext`・`ViewData`・コントローラー名を使う。共有の変換器は `RequestServices` から取る |
| `Cshtml2Pdf(HttpContext httpContext, string controllerName, string viewName, object? model)` | コントローラーの外(ミドルウェア等)から使う場合 |
| `Options`(`Cshtml2PdfOptions`) | 帳票1件ごとの体裁(下記) |
| `Task<HangaPdfDocument> GenerateAsync(CancellationToken)` | PDFのバイト列(`Content`)と警告の一覧(`Warnings`)を返す |
| `Task<byte[]> ToBytesAsync(...)` / `Task WriteToAsync(Stream, ...)` / `Task SaveAsync(string path, ...)` | 要件7.1。`SaveAsync` は同じフォルダの一時ファイルに書いてから名前を変える(失敗・取り消しで途中までのファイルを残さない。バッチの仕様の要件4.2) |
| `Task<IActionResult> ToActionResultAsync(string? fileName, PdfDisposition, ...)` | 要件7.2〜7.4 |

`Cshtml2Pdf` はスレッドセーフではない(要件9.1)。Utsushi の `Excel2Pdf` と同じく、帳票1件ごとに作る。`HangaPdfConverter` はスレッドセーフで、アプリ全体で1つを共有する。

### オプション

`HangaOptions`(アプリ全体。`AddHanga` で指定)

| 名前 | 既定 | 内容 |
|---|---|---|
| `ChromiumExecutablePath` | (必須) | Chromiumの実行ファイル(要件2.2) |
| `ChromiumArguments` | 空 | Chromiumに渡す追加の引数(`--no-sandbox` 等。環境に合わせて運用で決める) |
| `LaunchOnStartup` | `false` | アプリの起動時にChromiumを起動する(要件10.2) |
| `MaxConcurrentRenders` | 4 | 同時に処理する帳票の数(要件9.4)。設定ファイルで変えられる。決め方は下記「同時に処理する帳票の数」 |
| `RenderTimeout` | 30秒 | 表示の完了を待つ上限(要件4.3) |
| `AllowedExternalHosts` | 空 | 仮想オリジン以外で取得を許すホスト(要件3.4) |
| `GaijiFontFamily` / `GaijiFontFile` | 未指定 | 外字用フォント(名前 または ファイル。要件6.1) |
| `DetectMissingGlyphs` | `true` | 字形の無い文字を検出する(要件6.7) |
| `Strict` | `false` | 警告の対象をエラーにする(要件8.7) |
| `MaxTotalResponseBytesPerReport` | 200MB | 帳票1件の、仮想オリジンへの要求の応答の合計の上限(大きなファイルを多数読み込むページでメモリを使い尽くさないための安全弁) |
| `MaxResponseBodyBytes` | 50MB | 仮想オリジンへの要求1件の応答の大きさの上限(メモリを使い尽くさないための安全弁。下記「⑤⑥」) |
| `VirtualOrigin` | `https://hanga.invalid` | 仮想オリジン(`.invalid` はRFC 6761で実在しないことが保証されたドメイン) |

`Cshtml2PdfOptions`(帳票1件ごと)

| 名前 | 既定 | 内容 |
|---|---|---|
| `PaperSize` | `PaperSize.A4` | A3・A4・A5・B4・B5・Letter・Legal、または `PaperSize.Custom(幅mm, 高さmm)`(要件5.1) |
| `Orientation` | `Portrait` | `Landscape` で幅と高さを入れ替える |
| `Margins` | 上下左右 10mm | `PageMargins`(mm) |
| `Scale` | 1.0 | 拡大縮小の倍率。Chromiumの制約で 0.1〜2.0(要件5.3) |
| `FitToPageWidth` | `true` | 内容の幅が印刷可能な幅を超えるときだけ縮小する。`false` で無効にできる(要件5.4) |
| `SinglePage` | `false` | 内容の高さに合わせた1ページにする(要件5.5) |
| `CssMedia` | `Print` | `Print`(印刷用CSS)/ `Screen`(画面用CSS)(要件5.6) |
| `PrintBackground` | `true` | 要件5.7 |
| `PageNumbers` | `false` | フッターに「1 / 3」を出す(要件5.8) |
| `Title` | 未指定 | PDFの文書のタイトル。未指定ならページの `<title>`(要件5.9) |
| `ReadyExpression` | 未指定 | 完了条件のJavaScriptの式(要件4.2) |
| `Timeout` / `Strict` | 未指定 | 指定すれば `HangaOptions` の値を上書きする(要件4.3, 8.7) |

`FitToPageWidth` の既定は `true`(利用部門の判断。2026年10月4日): 画面の全体をPDFにしたいという要望(product.md「要件の前提」)に合うため。
内容が用紙に収まる画面では倍率が1のままで、影響しない。検証では、幅1800pxの表が、縮小しないと右端の列が全行切れた(下記「検証結果」)。

## 各部の設計

### ① 元の要求の写し取り(`RequestSnapshot`)

`Cshtml2Pdf` の作成時に、元の要求から次を写し取る。以降、元の `HttpContext` を別のスレッドから参照しない(要件9.3)。

- `Cookie` ヘッダー、`Host`、スキーム、パスベース、`Accept-Language`
- `RequestServices` から得た `IServiceScopeFactory`(要求ごとのスコープを作るため。ルートのプロバイダーに由来し、元の要求の終了後も使える)

スキームは、元の要求を処理したときにアプリが認識していたもの(`HttpRequest.Scheme`)をそのまま使う。
IISのリバースプロキシの背後で `UseHttpsRedirection` を使っているアプリでも、元の要求がリダイレクトされなかった以上、転送した要求もリダイレクトされない。

### ② ビューのHTML化(`Hanga.Templating.ViewHtmlRenderer`)

検証コード(`spikes/pdf-output-verification/mvc/`)と同じ手順。

1. `ActionContext` を作る。`RouteData` の `controller`・`action` に、コントローラー名とビュー名を入れる(ビューの探索場所がこれで決まる)。
2. `IRazorViewEngine.FindView(actionContext, viewName, isMainPage: true)`。見つからなければ `SearchedLocations` を含めて `HangaViewNotFoundException`(要件1.4)。
3. `ViewDataDictionary` は、コントローラーから作った場合はコントローラーの `ViewData` を引き継ぎ(`ViewBag` を含む)、`Model` を設定する。
4. `ViewContext` を作り、`IView.RenderAsync` で `StringWriter` に書き出す。描画中の例外は `HangaViewRenderingException` に包む(要件1.5)。

元の要求が無い場合(バッチ。要件1.6)に必要な `HttpContext` の作り方(何もしないサーバーでのホスト起動、ダミーのエンドポイント)は検証レポート「6.4」にあり、
バッチの仕様(`.kiro/specs/batch-pdf-generation/design.md`「帳票1件用の `HttpContext`」)で扱う。

### ③④ Chromiumとブラウザコンテキストの管理(`Hanga.Rendering.BrowserHost`)

- Chromiumのプロセスは1つを共有する(要件10.1)。最初の要求時(または `LaunchOnStartup`)に起動する(要件10.2)。起動の直後に版(`GetVersionAsync`)を `ILogger` に記録する(要件11.3)。
- 起動は `SemaphoreSlim(1)` で排他する。`Disconnected` を検知したら、次の利用時に排他のうえで1回だけ起動し直す(要件9.5)。
- ユーザーデータのフォルダは、プロセスごとに一時フォルダを作り、終了時に削除する。
- 帳票1件ごとに `CreateBrowserContextAsync()`(18.1.0での名前)で独立したコンテキストを作り、終了時(例外・取り消しを含む)に `CloseAsync()` する(要件9.2, 9.6)。
- 同時実行数は `SemaphoreSlim(MaxConcurrentRenders)` で制限する。待ち時間は `CancellationToken` で取り消せる(要件9.4, 9.6)。

### ⑤⑥ ページの表示と要求の振り分け(`Hanga.Rendering.ReportPage`)

ページを作ったら、要求への介入(`SetRequestInterceptionAsync(true)`)を有効にし、ページ内のJavaScriptで `location.href = '<仮想オリジン>/__hanga/report'` を実行して移動する(要件2.4)。
移動の完了は `WaitForNavigationAsync`(`Networkidle0`)で待つ。Chromiumからの要求は次のとおり振り分ける。

| 要求 | 応答 |
|---|---|
| `<仮想オリジン>/__hanga/report` | 帳票のHTML(⑧の注入を含む) |
| `<仮想オリジン>/__hanga/probe-a.ttf` 等、`/__hanga/` で始まるもの | Hangaが埋め込みリソースとして持つファイル(判定用フォント、外字用フォントのファイル) |
| `<仮想オリジン>/favicon.ico` | 204(空)。失敗として扱わない(要件3.7) |
| その他の `<仮想オリジン>/...` | `IVirtualOriginHandler` に渡す(下記)。状態コード400以上・300番台は失敗として記録し、⑦の後にエラーにする(要件3.5, 8.4) |
| `AllowedExternalHosts` のホスト | `ContinueAsync()`(ネットワークから取得。要件3.4) |
| それ以外 | `AbortAsync()`。警告 `BlockedExternalRequest`(厳格ならエラー。要件3.6) |

`IVirtualOriginHandler` の実装(`Hanga.PipelineForwarder`):

1. 写し取った `IServiceScopeFactory` で要求ごとのスコープを作り、`DefaultHttpContext` を作る(要件3.3)。
2. メソッド・パス・クエリ・Chromiumが付けたヘッダー(`Cookie`・`Host` を除く)・本文を設定し、写し取った `Cookie`・`Host`・スキーム・パスベースを付ける(要件3.2)。
3. 応答の本文は `MemoryStream` に受ける。上限(既定 50MB)を超えたら失敗とする。
4. 捕捉したパイプライン(`RequestDelegate`)を呼び、状態コード・`Content-Type`・本文を返す。

パイプラインの捕捉: `AddHanga` が登録する `IStartupFilter` が、パイプラインの先頭に `app.Use(next => { holder.Pipeline = next; return next; })` を加える。
`holder` は依存性注入のシングルトン(静的フィールドにしない。`tech.md`「スレッドセーフと同時実行」)。

### ⑦ 表示の完了の待機

次をすべて満たすまで待つ。全体の上限は `RenderTimeout`(要件4.1〜4.4)。

1. 移動の完了(`Networkidle0`: 500ミリ秒の間、通信中の要求が無い)
2. `ReadyExpression` が指定されていれば、その式が真になる(`WaitForFunctionAsync`)
3. `document.fonts.ready`

ページの `PageError` イベント(捕まえられていない例外)は警告 `ScriptError` にする(厳格ならエラー。要件4.5)。
上限を超えた場合は、待っていた条件と、未完了の要求のURL(認証情報を除く)を含めて `HangaTimeoutException`(要件4.4, 8.5)。

### ⑧ 外字・異体字・字形の無い文字(`Hanga.Rendering.GlyphSupport`)

表示の完了の後(APIから取得した値が描画された後)に、ページ内で次を実行する。表示の完了前に行うと、後から描画された文字が対象から漏れるため。

1. **外字用フォントの適用**(外字用フォントが設定されている場合。要件6.2, 6.3)
   - `@font-face{font-family:'HangaGaiji'; src:local('<名前>'); unicode-range:U+20000-3134F, U+E000-F8FF, U+F0000-10FFFF}` を加える。
     `GaijiFontFile` の場合は `src:url('/__hanga/gaiji')` とし、⑤の表のとおりファイルを返す(遅いため、名前での指定を推奨。要件10.3)。
   - 全要素の `font-family` の末尾に `'HangaGaiji'` を加える。
2. **異体字の包み込み**(要件6.4): 異体字セレクタ(U+E0100〜U+E01EF)付きの文字を `<span class="hanga-ivs">` で包み、範囲を絞らない外字用フォント(`HangaGaijiIvs`)で描く。
3. `document.fonts.ready` を待つ。
4. **字形の無い文字の検出**(`DetectMissingGlyphs`。要件6.7)
   - 判定用フォントA・B(全符号位置に、形の違う字形を1つずつ持つ小さなフォント。cmap format 13。各約650バイト)を `/__hanga/` から読み込む。判定用フォントは画面の表示には使わない。
   - ページの各文字(重複は1回)を、その要素の `font-family` の末尾に判定用フォントAを足したもの、Bを足したもので canvas に描き、結果を比べる。
     一致すれば要素のフォント(外字用フォントを含む)で描かれ、異なれば判定用フォントまで落ちた(= 指定したどのフォントにも字形が無い)とみなす。
   - OSの代替フォントは判定用フォントより後にしか使われないため、判定はOSに左右されない。このため、OSの代替フォントでたまたま描ける文字も「字形が無い」と判定する(要件6.5 の方針どおり、OSに頼る文字を知らせる)。
   - 見つかった文字は、符号位置を付けて警告 `MissingGlyph` にする(厳格ならエラー)。
   - 判定用フォントは、`tools/probe-fonts/` のスクリプト(Python + fontTools)で生成し、生成物(.ttf)をリポジトリに入れて `Hanga.Rendering` の埋め込みリソースにする。

外字用フォントがサーバーに無い場合(要件6.6): Chromiumの起動の直後に、`local('<名前>')` の `FontFace` を読み込んで状態を調べ、失敗すれば `HangaConfigurationException` とする。
`GaijiFontFile` の場合は `AddHanga` の時点でファイルの存在を確かめる。(要件6.6の「変換器の作成時」は、名前の指定についてはChromiumの起動時と読み替える。requirements.md を合わせて直す)

### ⑨ 体裁とPDF化(`Hanga.Rendering.PdfLayout`)

1. `CssMedia` に応じて `EmulateMediaTypeAsync(Print/Screen)` を呼ぶ(要件5.6)。
2. 用紙の寸法(mm)を決める。`Orientation = Landscape` なら幅と高さを入れ替える。PuppeteerSharp の `PaperFormat` と `Landscape` は使わない(要件5.2)。

   | 用紙 | 幅 × 高さ(mm) |
   |---|---|
   | A3 / A4 / A5 | 297×420 / 210×297 / 148×210 |
   | B4 / B5 | 257×364 / 182×257(日本の業務で使うJIS B列) |
   | Letter / Legal | 215.9×279.4 / 215.9×355.6 |

3. 倍率を決める(要件5.3, 5.4): 画面(ビューポート)の幅を印刷可能な幅(`(用紙の幅 − 左右の余白)mm ÷ 25.4 × 96` CSSピクセル)にしてから、
   内容の幅(`scrollWidth`)を測る。`FitToPageWidth` なら、`内容の幅 × Scale` が印刷可能な幅を超えるときだけ `印刷可能な幅 ÷ 内容の幅` に縮小する
   (収まるときは `Scale` のまま)。0.1〜2.0 に丸める。
   - 画面の幅を合わせてから測るのは、`width: 100%` のように用紙の幅に合わせて伸び縮みする画面を、不要に縮小しないため
     (PuppeteerSharp の既定の画面の幅 800px のまま測ると、印刷可能な幅 718px を超えるとみなしてしまう)。実装時に追加した(タスク7)。
4. `SinglePage` なら、画面の幅を `印刷可能な幅 ÷ 倍率`(縮小して印刷したときに内容が組まれる幅)にして内容の高さを測り、
   ページの高さを `内容の高さ × 倍率 ÷ 96 × 25.4 + 上下の余白 + 1`(mm)にする(要件5.5)。上限は `PaperSize.MaxMillimeters`(5000mm)で、超えた分は次のページに送られる。
5. `PageNumbers` なら、`DisplayHeaderFooter = true`、フッターに `<span class="pageNumber"></span> / <span class="totalPages"></span>` を指定し、ヘッダーは空にする(要件5.8)。
6. `Title` が指定されていれば `document.title` を書き換える(Chromiumは `document.title` を文書のタイトルにする。要件5.9)。
7. `PdfDataAsync`(幅・高さはmmの文字列、`PrintBackground`、`MarginOptions`、`Scale`)。

用紙の寸法の誤差: Chromiumは内部で丸めるため、A4(595.28×841.89pt)を指定しても 595.92×841.92pt になる(検証レポート「6.3」)。±1pt以内を許容し、テストもこの範囲で判定する。

### 生成結果と返し方(要件7, 8.6)

- `HangaPdfDocument`: `byte[] Content`、`IReadOnlyList<HangaWarning> Warnings`、`string ChromiumVersion`、`TimeSpan Elapsed`。
- `ToActionResultAsync`: `FileContentResult`(`application/pdf`)を返し、`Content-Disposition` を `ContentDispositionHeaderValue` で組み立てる。
  `inline`/`attachment` を選べ、ファイル名は ASCII の代替名(`filename`)と UTF-8 の名前(`filename*`、RFC 5987)の両方を設定する(要件7.3, 7.4)。
  警告は `ILogger` に記録する。

### 例外と警告(要件8)

```
HangaException(基底。Stage: 失敗した段階)
├── HangaConfigurationException      設定の誤り(Chromiumの場所、外字用フォントが無い等)
├── HangaViewNotFoundException       ビューが見つからない(探した場所の一覧)
├── HangaViewRenderingException      ビューの描画中の例外(内部例外)
├── HangaBrowserException            Chromiumの起動・命令の失敗(実行ファイルの場所・版)
├── HangaResourceRequestException    仮想オリジンへの要求の失敗(URL・状態コード)
├── HangaTimeoutException            表示の完了の待機の上限超過(待っていた条件)
└── HangaStrictModeException         厳格な扱いで、警告の対象をエラーにしたもの(警告の一覧)
```

- `Stage`: `ViewRendering`・`BrowserLaunch`・`PageLoad`・`ResourceRequest`・`Waiting`・`PdfOutput`・`Configuration`(要件8.2)。
- 警告 `HangaWarning`: `Kind`(`BlockedExternalRequest`・`ScriptError`・`MissingGlyph`・`Dialog`・`BlockedNavigation`・`SinglePageOverflow`)、`Message`、`Detail`。

### エッジケースの検証を受けて加えた設計(2026年10月4日)

`samples/Hanga.EdgeCases`(不具合が起きやすい帳票・使い方を集めたサンプル)で見つかった問題に対応した。

- **ダイアログ**(要件4.6): ページの `Dialog` の通知で、ダイアログを「OK」(`prompt` は既定値)で閉じ、警告 `Dialog` を記録する。
  ダイアログの文言は氏名などを含みうるため、詳細(`Detail`)に入れる(通常のログには出さない。要件8.5)。
- **ページの移動**(要件2.7): 帳票の URL への最初の移動の後、ページ全体(メインフレーム)の移動の要求には 204(No Content)で応え、警告 `BlockedNavigation` を記録する。
  要求を中断(abort)すると Chromium がエラーページへ移るため、ブラウザが元のページに留まる 204 を使う。内側のフレーム(`iframe`)の移動は止めない。
- **1ページ化の上限**(要件5.5): 内容の高さが用紙の上限(5000mm)を超える場合は、上限の高さで出力し(超えた分は次のページ)、警告 `SinglePageOverflow` を記録する。
  体裁は PDF の出力の段階で決まるため、厳格な扱いのエラーもこの段階で投げる。
- 例外のメッセージ・警告・ログに、`Cookie` の値やヘッダーの値を含めない。URL はクエリ文字列とフラグメントを除いて載せる(クエリに秘密の値を入れるアプリがありうるため。要件8.5)。

## Chromium のバージョンアップの検証ツール(要件11)

- `samples/Hanga.Sample`: 検証用のサンプルアプリ(ASP.NET Core 5 MVC)。サンプルの帳票ビュー(外部CSS・JavaScript・APIからの値の取得・外字・異体字・幅の広い表・複数ページ)と、
  テスト用のログイン、PDF用アクションを持つ。
- `tools/Hanga.ChromiumCheck`: 指定したChromiumでサンプルアプリをツールの中で起動し(ローカルホストのみ)、各PDF用アクションを呼んでPDFを集め、基準の結果と比べる。
  - 比べるもの: ページ数、ページサイズ(±1pt)、取り出した文字列、各文字の位置(±0.5pt)と描いたフォント。
  - 当初は、同じ帳票の画面をChromiumで画面写真にして比べる設計だった。しかし実装して確かめると、画面写真はアプリの画面そのもの(Hangaの外字の組み込み・幅の縮小などを経ない)を撮るため、
    PDFの変化を捉えられなかった(外字用フォントを外しても画面写真は変わらなかった)。このため、PDFそのものの各文字の位置とフォントを比べる方式に変えた(タスク12)。
    PDFを画像にするライブラリは使わない。気になる違いは、運用部門がPDFを開いて目で確かめる。
  - PDFからの文字列・文字の位置・フォントの取り出しには PdfPig(Apache-2.0)を使う(`tech.md` に記録済み)。
- 使い方は `docs/Chromium更新前の検証手順.md` にまとめる(要件11.4)。

## テスト戦略

- **ユニットテスト**(Chromium不要): 用紙の寸法と向き、倍率・1ページの高さの計算、`Content-Disposition` の組み立て、要求の写し取り、要求の振り分けの判定、警告と厳格な扱いの切り替え、オプションの上書き。
- **結合テスト**(Chromiumを使う): `samples/Hanga.Sample` をテスト用のホストで起動し、PDF用アクションを呼んで、ページ数・ページサイズ・文字列・警告を確かめる。
  同時要求での取り違えが無いこと(要件9)、ブラウザコンテキストの分離(localStorage)、Chromiumの再起動(プロセスを止めてから次の要求)も確かめる。
  Chromiumの場所は環境変数(`HANGA_TEST_CHROMIUM`)で渡す。CIでは、CIの環境にあるChromeを使う。
- PDFのバイト列の完全一致では比べない(生成日時・Chromiumの版で変わるため)。
- テストのパッケージは net5.0 世代(`Microsoft.NET.Test.Sdk` 17.1.0 / `xunit` 2.4.1 / `xunit.runner.visualstudio` 2.4.3)。

## 検証結果(設計の根拠)

2026年10月4日。PuppeteerSharp 18.1.0、Chrome 141。検証コード: `spikes/pdf-output-verification/design-checks/`。

| 項目 | 結果 |
|---|---|
| ブラウザコンテキストの分離 | 帳票ごとにコンテキストを作ると、別の帳票の localStorage は見えなかった(`null`)。既定のコンテキストを使い回すと、前の帳票が保存した値(`operator-C`)が見えた |
| 印刷用CSS / 画面用CSS | 既定(印刷)では `@media print` の要素だけ、`EmulateMediaTypeAsync(Screen)` では画面用の要素だけが出た |
| 幅を収める | 幅1800pxの表(120行): 縮小しないと右端の列が120行すべて欠けた。倍率0.399で全列・全行が出た |
| 1ページ化 | 内容の高さから計算した高さ(287mm)で、1ページのPDFになった |
| 異体字 | 「葛」と「葛+U+E0102」、「辻」と「辻+U+E0102」が、異なる字形で描かれた(IPAmj明朝で既定と異なる字形が割り当てられた組み合わせ)。「葛+U+E0100」は既定と同じ字形が割り当てられているため、比較に使えない |
| 字形の無い文字の検出 | 外字用フォントなし: 「𠮷」・U+E000・U+0379 を検出。外字用フォントあり: U+E000・U+0379 のみ(「𠮷」は外字用フォントで描かれる)。単純に「未割り当ての文字を描いた結果」と比べる方式は、OSのフォント(Unifont)が符号位置ごとに異なる字形を描くため使えなかった |
| Chromiumを使い回した場合の時間 | 1件ずつ: 約1.15秒/件(5件)。8件同時: 合計約1.9秒(ビューのHTML化・APIの処理を除く) |

### 同時に処理する帳票の数(`MaxConcurrentRenders`)

2026年10月4日、この開発環境(CPU 4コア、メモリ16GB、Linux)で、Chromiumを使い回し、同時に処理する数を変えて計測した(検証コード: `design-checks/` の `concurrency`)。
帳票は60行の表1つ。ビューのHTML化・APIの処理は含まない。

| 同時に処理する数 | Chromium全体の実メモリ | 20件を処理する時間 | 1件あたり |
|---|---|---|---|
| (待機中) | 約690MB | — | — |
| 1 | 約830MB | 23.6秒 | 1.18秒 |
| 2 | 約970MB | 12.3秒 | 0.62秒 |
| 4 | 約1,230MB | 7.1秒 | 0.36秒 |
| 8 | 約1,750MB | 5.1秒 | 0.25秒 |
| 16 | 約2,780MB | 3.8秒 | 0.19秒 |

- 実メモリは、Chromiumの各プロセスのRSSの合計(共有部分を重複して数えるため、実際より多めの値)。同時に1件増えるごとに、約65〜130MB増えた。
- 1件の処理時間の多くは、通信が止んでから500ミリ秒待つ「ネットワークの静止」の待ち時間で、CPUを使っていない。このため、CPUのコア数(4)を超えて同時に処理しても、処理量は増えた。
- したがって、上限を決める主な要因は **メモリ** である。

既定値は4とする(利用部門の判断。2026年10月4日)。上の計測では、4件同時でChromium全体が約1.2GBで、処理量も1件ずつの約3倍になる。
本番サーバーのメモリと、同時にPDFを要求するオペレーターの数に合わせて、設定ファイルで変える。
目安: `上限 ≒ (Chromiumに割り当てられるメモリ − 約700MB) ÷ 130MB`。本番サーバー(Windows)でのメモリの使い方はLinuxと異なる可能性があるため、検証環境で計測し直して調整する。
調整の手順は、利用の手引き(`docs/ライブラリの使い方.md`)に書く。

### 処理時間の目安(要件10.4)

2026年10月4日、この開発環境(CPU 4コア、Linux、Chrome 141)で、テスト用アプリの PDF 用アクション(`/Pdf/Order`。レイアウト・外部CSS・JavaScript・
ログインが必要なAPIからの値の取得を含む)を呼んで計測した(`tests/Hanga.Tests/PerformanceTests.cs`)。

| 場合 | 時間 |
|---|---|
| 初回(Chromium の起動を含む) | 約1.9秒 |
| 2回目以降(ビューのHTML化・APIの処理・ページの表示・PDF化を含む) | 約1.2秒/件 |

1件の時間の多くは、通信が止んでから 500 ミリ秒待つ「ネットワークの静止」の待ち時間である(上記「同時に処理する帳票の数」)。

### レビューを受けて加えた設計(2026年10月4日)

`code-reviewer`・`security-reviewer` の指摘を受けて、次を加えた。

- **想定外の失敗の包み込み**: ページの表示・PDFの出力の中で PuppeteerSharp が投げた例外(Chromium が描画中に落ちた、`ReadyExpression` の書き間違い等)は、
  失敗した段階と Chromium の場所・版を付けて `HangaBrowserException` にする(要件8.1〜8.3)。
- **帳票ごとの取り消し**: 帳票ごとに、元の取り消しとつないだ取り消しを作り、帳票の処理が終わったら(正常・失敗とも)取り消す。
  時間切れの後も、アプリへの転送(オペレーターの権限での API の処理)が続かないようにするため(要件9.6)。
- **出力の直前の確認**: PDF の出力の直前に、完了していない要求が無くなるのを待ち、失敗した要求が無いことを確かめ直す(待機の後に始まった要求を見逃さないため。要件8.4)。
- **許可した外部ホストへの要求の失敗**: 状態コード400以上・名前解決の失敗などを、仮想オリジンへの要求と同じく失敗として扱う(要件3.5 に追記)。
- **`data:`・`blob:` の URL**: ページの中で完結するデータのため、遮断せずに通す。
- **応答の合計の上限**: `MaxTotalResponseBytesPerReport`(既定200MB)を超えた応答は失敗として扱う。
- **入れ子の検出**: 転送した要求の `HttpContext.Items` に印を付け、その中で `Cshtml2Pdf` を作ろうとした場合はすぐに `HangaConfigurationException` にする
  (帳票のビューが PDF 用アクションを iframe 等で読み込むと、同時実行の枠を食い合うため)。
- **ログ**: 警告は、通常のログ(Warning)には種類と件数だけを出し、詳細(字形の無い文字・スクリプトの例外の内容。氏名などを含みうる)は Debug に出す(要件8.5)。
- **Chromium の起動し直し**: ブラウザコンテキストを作れなかった場合は、プロセスの終了の通知が届いていなくても、その Chromium を破棄して 1 回だけ起動し直す(要件9.5)。
- **一時ユーザーデータフォルダ**: 起動時に、異常終了などで残った 1 日以上前のフォルダを削除する。

## 未検証の事項(実装時・Windows Server の検証環境で確認する)

- 判定用フォント(cmap format 13 のみ)を、Windows の Chromium が読み込めるか。読み込めない場合は、format 12 で全符号位置を連続した字形に割り当てる形に作り直す(ファイルは大きくなる)。
- Windows でのインストール済みフォントの名前での参照(`local('IPAmj明朝')`)。
- IISの配下(アプリケーションプールのユーザー)でのChromiumの起動。
- 画面のAPIが POST と Antiforgery のトークンを使う場合の動作。
- Chromiumを使い回し、多数の同時要求を受けた場合の安定性。
- (セキュリティレビューの提案、未対応)Chromium の起動引数の既定に `--host-resolver-rules`(許可したホスト以外を名前解決させない)を加える多重の防御。
  WebSocket・Service Worker など、ページの要求への介入を通らない通信を止めるため。起動引数の既定を変えると利用部門の環境での影響を確かめる必要があるため、今回は見送った。
  必要であれば `ChromiumArguments` で指定できる。
- PuppeteerSharp 18.1.0 は要求の本文を文字列で渡すため、バイナリの本文(ファイルのアップロードなど)を送る POST は正しく転送できない。帳票の表示時に行う API の呼び出しでは想定していない。

## 決めてほしいこと

- `RenderTimeout` の既定(30秒)、余白の既定(上下左右10mm)。
