# 設計書: バッチでの帳票PDFの一括生成

対応する要件: `requirements.md`(要件1〜6)。中核機能の設計(`.kiro/specs/view-pdf-generation/design.md`)の部品をそのまま使い、本書は加える・変える点だけを書く。
方式の根拠は `docs/PDF出力方式検証レポート.md`「6.4」「6.5」。

## 概要

バッチは、Hangaの公開API `HangaBatch`(バッチ用の変換器)を起動し、帳票1件ごとに `Cshtml2Pdf` を作ってファイルに保存する。

```
バッチ(Webアプリとは別の実行ファイル)
  │ HangaBatch.StartAsync(...)                                  … 要件1
  │   ├ バッチのプロセスの中に、Webサーバーを持たないASP.NET Coreのホストを作る
  │   │   (MVCのビュー描画・静的ファイル・ルーティング。ポートを開かない「何もしないサーバー」)
  │   ├ AddHanga と同じ部品を登録し、そのホストのパイプラインを捕まえる
  │   └ Chromium を起動する(起動できなければここで例外)
  │
  │ 帳票1件ごと(並行してよい):
  │   new Cshtml2Pdf(batch, "Invoice", "Invoice", model)        … 要件1.5, 2.3
  │   await pdf.SaveAsync(path)                                 … 要件4
  │     ① 帳票1件用の依存性注入のスコープと HttpContext を作る   … 要件2.4
  │     ②〜⑩ 中核機能と同じ(ビューのHTML化 → Chromium → PDF)
  │        仮想オリジンへの要求は、ホストのパイプラインにプロセス内で渡す(Cookie なし) … 要件3
  │     ⑪ 一時ファイルに書いてから保存先の名前に変える        … 要件4.2
  │
  │ await batch.DisposeAsync()                                  … 要件1.4(Chromium の終了)
```

中核機能との違いは、(a) アプリのパイプラインを呼び出し元のWebアプリから借りず、Hangaがバッチの中に作ること、
(b) 元の要求が無いため、帳票1件ごとに `HttpContext` を作り、Cookie などのオペレーターの情報を付けないこと、の2点だけである。
ビューのHTML化(`ViewHtmlRenderer`)・ページの表示から PDF まで(`ReportRenderer`)・要求の転送(`PipelineForwarder`)は、中核機能のコードを変えずに使う。

## プロジェクト構成とレイヤー

新しいプロジェクトは作らない。バッチ用の部品はファサード `Hanga` に置く(ASP.NET Core のホストを扱うため)。

| 置き場所 | 型 | 公開 | 内容 |
|---|---|---|---|
| `src/Hanga/` | `HangaBatch` | 公開 | バッチ用の変換器。起動・終了、帳票1件用のスコープと `HttpContext` の作成 |
| `src/Hanga/` | `HangaBatchOptions` | 公開 | バッチ固有の設定(ビューのアセンブリ・静的ファイルのフォルダ・サービスの追加登録) |
| `src/Hanga/` | `Cshtml2Pdf` | 公開(変更) | バッチ用のコンストラクターを加える。ファイルへの保存を一時ファイル経由にする |
| `src/Hanga/Hosting/` | `BatchHostBuilder` | internal | Webサーバーを持たないホストの組み立て |
| `src/Hanga/Hosting/` | `NoopServer` | internal | ポートを開かない `IServer` の実装 |
| `src/Hanga/Hosting/` | `RequestSnapshot` | internal(変更) | バッチ用に、`HttpContext` から写し取る(既存の `From` をそのまま使う) |

- `Hanga.Core`・`Hanga.Templating`・`Hanga.Rendering` は変更しない。
- 公開APIに出す型は、ASP.NET Core の型(`IServiceCollection`)と .NET の型(`Assembly`)に限り、PuppeteerSharp の型を出さない(要件6.3)。
- 新しい依存ライブラリは加えない(要件6.1)。使う ASP.NET Core の機能(汎用ホスト・`IServer`・静的ファイル・静的Webアセット)は、すべて共有フレームワーク `Microsoft.AspNetCore.App` 5.0 に含まれる。

## 公開API

### 起動と終了

```csharp
// 設定は Webアプリと同じ HangaOptions(要件1.2)。設定ファイルから読む場合は configuration.GetSection("Hanga").Bind(options)
var options = new HangaOptions
{
    ChromiumExecutablePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
    GaijiFontFamily = "IPAmj明朝",
    MaxConcurrentRenders = 4,
};

await using var batch = await HangaBatch.StartAsync(options, b =>
{
    b.ViewAssemblies.Add(typeof(InvoiceModel).Assembly);         // 帳票ライブラリ(要件2.2)
    b.ConfigureServices = services => services.AddLogging(l => l.AddConsole());  // 任意(要件2.5)
});
```

| メンバー | 内容 |
|---|---|
| `static Task<HangaBatch> StartAsync(HangaOptions options, Action<HangaBatchOptions>? configure = null, CancellationToken cancellationToken = default)` | ホストを作って起動し、Chromium を起動する。設定の誤り・Chromium の起動の失敗は、この時点で例外にする(要件1.3) |
| `string? ChromiumVersion` | 起動した Chromium の版 |
| `ValueTask DisposeAsync()` / `void Dispose()` | ホストを停止し、Chromium を終了する(要件1.4)。生成中の帳票がすべて終わってから呼ぶ |

`HangaBatch` はスレッドセーフで、バッチ全体で1つを共有する(要件1.5)。終了した後に使うと `ObjectDisposedException` にする。

### 帳票1件ごと

```csharp
foreach (var customer in customers)
{
    var model = BuildInvoiceModel(customer);                      // 帳票に載せる値はすべてモデルで渡す
    var pdf = new Cshtml2Pdf(batch, "Invoice", "Invoice", model); // コントローラー名・ビュー名(要件2.3)
    pdf.Options.Orientation = PageOrientation.Portrait;           // 体裁は Webアプリと同じ(Cshtml2PdfOptions)
    try
    {
        await pdf.SaveAsync(Path.Combine(outputDir, customer.Id + ".pdf"), cancellationToken);
    }
    catch (HangaException ex)
    {
        // 失敗の扱い(飛ばす・止める・記録する)はバッチ側で決める(要件4.3)
        logger.LogError(ex, "帳票を作れませんでした: {Customer}", customer.Id);
    }
}
```

| メンバー | 内容 |
|---|---|
| `Cshtml2Pdf(HangaBatch batch, string controllerName, string viewName, object? model = null)` | バッチ用。ビュー名に `~/Views/...cshtml` の形のパスも指定できる(既存のコンストラクターと同じ) |
| `GenerateAsync` / `ToBytesAsync` / `WriteToAsync` / `SaveAsync` | 既存と同じ(要件4.1) |
| `ToActionResultAsync` | Webアプリ用。バッチでは使わない |

既存のコンストラクター(`Cshtml2Pdf(Controller, ...)`・`Cshtml2Pdf(HttpContext, ...)`)と同じ形にし、Webアプリの開発者が覚えることを増やさない。

### バッチ固有の設定(`HangaBatchOptions`)

| 名前 | 既定 | 内容 |
|---|---|---|
| `ViewAssemblies` | 空 | ビューを持つアセンブリ(帳票ライブラリ)。関連するビューのアセンブリ(`<名前>.Views.dll`)も含める。空なら MVC の既定の規則で探す(要件2.2) |
| `ContentRootPath` | バッチの実行ファイルのフォルダ(`AppContext.BaseDirectory`) | ホストのコンテンツのルート。Webルートの相対パスの基準 |
| `WebRootPath` | `wwwroot`(コンテンツのルートからの相対) | 静的ファイルを置くフォルダ(要件3.3)。発行したバッチでは、帳票ライブラリの静的Webアセットが `wwwroot/_content/<ライブラリ名>/` に置かれる |
| `StaticFileMappings` | 空 | URLのパス(`/` で始まる)→ フォルダ の対応の追加(要件3.3)。Webアプリの発行先の `wwwroot` を直接使う場合などに使う |
| `ConfigureServices` | null | ホストの依存性注入への追加の登録(ビューが `@inject` で使うサービス、ログの出力先、`WebEncoderOptions` など。要件2.5) |

`StartAsync` の時点で検証し、誤り(`WebRootPath`・`StaticFileMappings` のフォルダが無い、URLのパスが `/` で始まらない)は、すべての誤りを含めて `HangaConfigurationException` にする。
`HangaOptions` の検証(`Validate`)も同じ時点で行う。

## 各部の設計

### ホストの組み立て(`Hosting.BatchHostBuilder`。要件1.1, 2.1, 2.2, 3.1〜3.3)

検証コード(`spikes/batch-rcl-verification/`)と同じ手順で、汎用ホスト(`HostBuilder`)に Web のホストを組み込む。

1. `UseContentRoot(ContentRootPath)`、`WebRootPath` を指定した場合は `UseWebRoot`。
2. `UseStaticWebAssets()` を常に呼ぶ。開発中の実行(ビルドの出力)では `<バッチ名>.StaticWebAssets.xml` に従って帳票ライブラリの `wwwroot` を `/_content/<ライブラリ名>/` に重ねる。
   発行したバッチにはこの一覧が無く、何もしない(発行先の `wwwroot/_content/` から応答する)。
3. `UseServer(new NoopServer())`: ポートを開かない(要件1.1)。ホストの起動でルーティングなどの初期化だけが行われる。
4. サービス: `AddControllersWithViews()`、`ViewAssemblies` の各アセンブリと関連するビューのアセンブリをアプリケーションパーツに加える
   (同じ種類・同じ名前の部品が既にあれば加えない)、`AddHanga(options)` と同じ部品の登録(パイプラインを捕まえる `IStartupFilter` を含む)、`ConfigureServices`。
5. パイプライン: `UseStaticFiles()`(Webルート)→ `StaticFileMappings` ごとに `UseStaticFiles`(`RequestPath` とフォルダ)→ `UseRouting()` → `UseEndpoints(MapControllers)`。
   捕まえたパイプラインの先頭は Hanga の `IStartupFilter` が加える(中核機能と同じ)。
6. ホストを起動し(`StartAsync`)、続けて Chromium を起動する(`WarmUpAsync`。要件1.3)。失敗した場合はホストを停止・破棄してから例外を投げる。

- ルーティング(`UseRouting`・`UseEndpoints`)は、タグヘルパーの URL の生成(`asp-controller`/`asp-action`)に必要(検証レポート「6.4」)。
  バッチにコントローラーが無い場合、生成される URL は空になる(帳票では通常使わない)。
- 環境名は既定(`Production`)のまま。レイアウトの `<environment>` タグヘルパーは本番と同じ側が選ばれる。
- ホストの設定ファイル(`appsettings.json`)・環境変数は読まない(Hanga の設定は `HangaOptions` で受け取る)。ログの出力先は `ConfigureServices` で登録する。

### 帳票1件用の `HttpContext`(要件2.4, 3.4)

`Cshtml2Pdf` のバッチ用のコンストラクターは、`HangaBatch` と引数を保持するだけにする。`GenerateAsync` の中で次を行い、終わったらスコープを破棄する。

1. ホストのサービスから依存性注入のスコープを作る。
2. `DefaultHttpContext` を作り、`RequestServices` にスコープを設定する。スキームとホストは仮想オリジン(`HangaOptions.VirtualOrigin`)の値にする。
   ビューが絶対URLを生成した場合も、仮想オリジン(= 帳票のページのオリジン)を指すため、Chromium からの要求はパイプラインに渡る。
3. ダミーのエンドポイントを設定する(`SetEndpoint`)。タグヘルパーの URL の生成がエンドポイントルーティングの仕組みを使うようにするため(検証レポート「6.4」)。
4. `RequestSnapshot.From(httpContext)` で写し取る。Cookie・`Accept-Language` は空になり、パイプラインへの転送で付かない(要件3.4)。
5. 中核機能の `HangaPdfConverter.GenerateAsync` を呼ぶ(ビューのHTML化・ページの表示・PDF化)。

`HttpContext.User` は認証されていない空のユーザー、`Session` は使えない(使うと `HangaViewRenderingException`)。ビューが使う値はモデルから渡す(要件「対象外」)。
取り消しは、呼び出し元が渡した `CancellationToken` だけを使う(元の要求の取り消しが無いため)。

### ファイルへの保存(`Cshtml2Pdf.SaveAsync`。要件4.2)

1. PDF を生成する(失敗した場合は何も作らない。既存の動作)。
2. 保存先と同じフォルダに、一時ファイル(`<保存先のファイル名>.<ランダムな文字列>.tmp`)を作って書き込む。
3. `File.Move(一時ファイル, 保存先, overwrite: true)` で保存先の名前に変える。同じフォルダ内の名前の変更のため、途中までのPDFが保存先に見えることは無い。
4. 2・3 で例外(取り消しを含む)が起きた場合は、一時ファイルを削除してから例外を投げ直す。既にある保存先のファイルは変わらない。

Webアプリで使う場合の `SaveAsync` も同じ処理になる(中核機能の要件7.1 の改善。Webアプリの動作に影響は無い)。

### 失敗の扱い(要件4.3〜4.5)

Hanga は失敗の方針を持たず、帳票1件ごとに中核機能の例外(`HangaException` の派生。段階 `Stage` 付き)を投げる。
1件の失敗が後の帳票に影響しないことは、中核機能の次の設計で成り立つ。バッチの結合テストで、失敗の後に続けて生成できることを確かめる。

- 帳票ごとのブラウザコンテキストの作成と破棄(例外・取り消しを含む。中核機能「③④」)。
- Chromium のプロセスが終了していた場合の、排他のうえでの起動し直し(中核機能「③④」)。
- 帳票ごとの依存性注入のスコープ(上記「帳票1件用の `HttpContext`」)。

### 並行処理と性能(要件5)

- Chromium のプロセスは `HangaBatch` の起動から終了まで1つを使い回す(中核機能の `BrowserHost`)。
- 呼び出し元が帳票を並行して生成すると、Chromium での処理は `MaxConcurrentRenders` 件までに制限され、残りは待つ(中核機能の要件9.4)。
- ビューのHTML化は同時処理数の枠を取る前に行う。数千件を一度に `Task.WhenAll` で始めると、全件のモデルとHTMLが同時にメモリに載る。
  このため、呼び出し元が並行数を `MaxConcurrentRenders` の2倍程度に抑える書き方(`SemaphoreSlim`)を、利用の手引きに例として載せる。
  ライブラリの側で並行数を制御する入口(一覧を渡すとまとめて処理する API)は作らない。失敗の扱いを呼び出し元に任せる方針(要件4.3)と合わせるため。

## 帳票ライブラリとバッチの作り方(要件6.5。利用の手引きに書く)

検証結果(下記「検証結果」)から、次の作り方を推奨する。

- **帳票ライブラリ**: `Microsoft.NET.Sdk.Razor`、`<AddRazorSupportForMvc>true</AddRazorSupportForMvc>`、`net5.0`。
  `Views/<コントローラー名>/<ビュー名>.cshtml`、`Views/_ViewImports.cshtml`、`Views/_ViewStart.cshtml`、帳票用のレイアウト(`Views/Shared/`)、`wwwroot/` の静的ファイルを持つ。
  静的ファイルは `~/_content/<ライブラリ名>/...` で参照する(Webアプリとバッチで同じURLになる)。
  レイアウトは帳票ライブラリに置く(Webアプリの `_Layout.cshtml` はバッチから見えないため)。
- **バッチ**: `Microsoft.NET.Sdk.Web`(出力はコンソールアプリ)で作り、帳票ライブラリをプロジェクト参照する。
  この SDK なら、帳票ライブラリのビューが自動で見つかり、発行したときに静的Webアセットが `wwwroot/_content/<ライブラリ名>/` に置かれる。
  普通のコンソールアプリの SDK(`Microsoft.NET.Sdk`)では、ビューは `ViewAssemblies` の指定で使えるが、静的Webアセットが `_content` の下に置かれないため、
  `StaticFileMappings` で帳票ライブラリの `wwwroot` を `/_content/<ライブラリ名>` に対応付ける必要がある。
- Webアプリのプロジェクトに置いたままのビューを使う場合(帳票ライブラリに分けない場合)は、バッチから Webアプリのプロジェクトを参照し、
  `StaticFileMappings`(または `WebRootPath`)で Webアプリの `wwwroot` を指定する。ビューが Webアプリの `_Layout.cshtml` やサービスに依存する場合は、それもバッチで用意する必要がある。

## テスト戦略

- **テスト用の帳票ライブラリ** `tests/Hanga.TestReports`(Razor クラスライブラリ): 帳票ビュー(レイアウト・`_ViewStart`・部分ビュー・`@inject`・
  `~/_content/...` の CSS と JavaScript・`asp-append-version`)と静的ファイル。
- **結合テスト**(`tests/Hanga.Tests`、Chromium を使う): テストは `RollForward` で .NET 10 のランタイムで動き、.NET 5 形式の静的Webアセットの一覧を読まない
  (下記「検証結果」)。このため、静的ファイルは `StaticFileMappings`(帳票ライブラリの `wwwroot` をテストの出力にコピーしたもの)と、
  発行した形を模した `WebRootPath`(`_content/<ライブラリ名>/` の下に置いたもの)の2通りで与える。
  - モデルの値・CSS・JavaScript が PDF に反映されること、`@inject` のサービスが使えること
  - 並行して生成した帳票に、それぞれのモデルの値だけが入ること
  - ビューが無い・ビューの例外・存在しない静的ファイルの要求で、原因の分かる例外になり、その後の帳票は正常に生成できること
  - 生成の途中で Chromium のプロセスが終了しても、次の帳票は生成できること
  - `SaveAsync`: 失敗・取り消しで保存先にファイルができず、既存のファイルが壊れず、一時ファイルが残らないこと
  - 設定の誤り(フォルダが無い、Chromium が無い)が `StartAsync` で例外になること、終了後の利用が `ObjectDisposedException` になること
  - 処理時間(1件ずつ・並行)の計測
- 開発中の実行(静的Webアセットの一覧)と発行したバッチの動作は、検証コードで .NET 5 のランタイムで確かめた(テストでは扱わない)。

## 検証結果(設計の根拠)

2026年10月4日、この開発環境(Linux)で、`spikes/batch-rcl-verification/` を使って確かめた。詳細は検証レポート「6.5」。

| バッチの SDK・実行のしかた | ビューの発見 | `/_content/Reports/css/report.css` |
|---|---|---|
| `Microsoft.NET.Sdk`、ビルドの出力 | 自動では見つからない。アセンブリを指定すれば見つかる | 404(静的Webアセットの一覧が作られない) |
| `Microsoft.NET.Sdk`、発行 | 同上 | 404(`wwwroot/css/report.css` に置かれ、`_content` の下に無い) |
| `Microsoft.NET.Sdk.Web`、ビルドの出力、.NET 5.0.17 のランタイム | 自動で見つかる | 200(静的Webアセットの一覧 `BatchWeb.StaticWebAssets.xml` から) |
| `Microsoft.NET.Sdk.Web`、ビルドの出力、.NET 10 のランタイム(`RollForward`) | 自動で見つかる | 404(.NET 10 は新しい形式の一覧しか読まない。テスト環境だけの事情) |
| `Microsoft.NET.Sdk.Web`、発行 | 自動で見つかる | 200(`wwwroot/_content/Reports/css/report.css`)。`asp-append-version` も付いた |

- 帳票ライブラリのビューは、.NET 5 の Razor SDK では別のアセンブリ(`Reports.Views.dll`)にコンパイルされる。アセンブリを指定する場合は、関連するビューのアセンブリも加える必要があった。
- 処理時間の目安は、実装後に計測して下表に記録する(要件5.3)。

### 処理時間の目安(要件5.3)

(タスク5で計測して記入する)

## 未検証の事項(Windows の検証環境で確認する)

- Windows 上の .NET 5 ランタイムでの、発行したバッチの動作(静的Webアセットの配置を含む)。
- Visual Studio 2019 で作った帳票ライブラリ・バッチのプロジェクトでの動作(.NET 5 SDK の Razor SDK)。
- タスクスケジューラから、バッチ用のユーザーで起動した場合の Chromium の起動(必要な起動引数)。
