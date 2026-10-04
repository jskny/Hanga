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

新しいプロジェクトは作らない。バッチ用の部品はファサード `Hanga` に置く(ASP.NET Coreのホストを扱うため)。

| 置き場所 | 型 | 公開 | 内容 |
|---|---|---|---|
| `src/Hanga/` | `HangaBatch` | 公開 | バッチ用の変換器。起動・終了、帳票1件用のスコープと `HttpContext` の作成 |
| `src/Hanga/` | `HangaBatchOptions` | 公開 | バッチ固有の設定(ビューのアセンブリ・静的ファイルのフォルダ・サービスの追加登録) |
| `src/Hanga/` | `Cshtml2Pdf` | 公開(変更) | バッチ用のコンストラクターを加える。ファイルへの保存を一時ファイル経由にする |
| `src/Hanga/Hosting/` | `BatchHostBuilder` | internal | Webサーバーを持たないホストの組み立て |
| `src/Hanga/Hosting/`(`BatchHostBuilder.cs`) | `NoopServer` | internal | ポートを開かない `IServer` の実装 |
| `src/Hanga/Hosting/`(`BatchHostBuilder.cs`) | `BatchHostLifetime` | internal | 何もしない `IHostLifetime`(既定の `ConsoleLifetime` の代わり) |
| `src/Hanga/`(`HangaBatch.cs`) | `BatchRequest` | internal | 帳票1件用のスコープと `HttpContext`(非同期で破棄する) |
| `src/Hanga/Hosting/` | `RequestSnapshot` | internal(変更なし) | バッチでも、帳票1件用の `HttpContext` から既存の `From` で写し取る |

- `Hanga.Core`・`Hanga.Templating`・`Hanga.Rendering` は変更しない。
- 公開APIに出す型は、ASP.NET Coreの型(`IServiceCollection`)と .NET の型(`Assembly`)に限り、PuppeteerSharp の型を出さない(要件6.3)。
- 新しい依存ライブラリは加えない(要件6.1)。使う ASP.NET Coreの機能(汎用ホスト・`IServer`・静的ファイル・静的Webアセット)は、すべて共有フレームワーク `Microsoft.AspNetCore.App` 5.0 に含まれる。

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
    // ファイルの書き込みの失敗(IOException・UnauthorizedAccessException)は Hanga の例外に包まずに出る(要件4.3)
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

`StartAsync` の時点で検証し、誤り(`ViewAssemblies` に関連するビューのアセンブリ(`<名前>.Views.dll`)が配置されていない、`ContentRootPath`・`WebRootPath`・`StaticFileMappings` のフォルダが無い、URLのパスが `/` で始まらない、`ViewAssemblies` に null がある)は、すべての誤りを含めて `HangaConfigurationException` にする。
`HangaOptions` の検証(`Validate`)も同じ時点で行う。

## 各部の設計

### ホストの組み立て(`Hosting.BatchHostBuilder`。要件1.1, 2.1, 2.2, 3.1〜3.3)

検証コード(`spikes/batch-rcl-verification/`)と同じ手順で、汎用ホスト(`HostBuilder`)に Web のホストを組み込む。

1. `UseContentRoot(ContentRootPath)`、`UseWebRoot(WebRootPath。未指定なら wwwroot)`、環境名(`Production`)を、Webのホストに明示する(下記の環境変数の扱い)。
   アプリの名前(`WebHostDefaults.ApplicationKey`)は、バッチの実行ファイル(エントリのアセンブリ)の名前を、`Configure` の後に明示する。
   `Configure` は、渡した処理を書いたアセンブリ(`Hanga`)をアプリの名前にしてしまい、ビューの自動の発見と開発中の静的Webアセットの一覧の探索が働かなかった
   (エッジケースの検証で見つかった不具合。テストでは `ViewAssemblies` を必ず指定していたため見逃していた)。
2. `UseStaticWebAssets()` を常に呼ぶ。開発中の実行(ビルドの出力)では `<バッチ名>.StaticWebAssets.xml` に従って帳票ライブラリの `wwwroot` を `/_content/<ライブラリ名>/` に重ねる。
   発行したバッチにはこの一覧が無く、何もしない(発行先の `wwwroot/_content/` から応答する)。
3. `UseServer(new NoopServer())`: ポートを開かない(要件1.1)。ホストの起動でルーティングなどの初期化だけが行われる。
4. サービス: `AddControllersWithViews()`、`ViewAssemblies` の各アセンブリと関連するビューのアセンブリをアプリケーションパーツに加える
   (同じ種類・同じ名前の部品が既にあれば加えない)、`AddHanga(options)` と同じ部品の登録(パイプラインを捕まえる `IStartupFilter` を含む)、`ConfigureServices`。
5. パイプライン: `UseStaticFiles()`(Webルート)→ `StaticFileMappings` ごとに `UseStaticFiles`(`RequestPath` とフォルダ)→ `UseRouting()` → 404 を返す終端 →
   `UseEndpoints(MapControllers + MapDefaultControllerRoute)`(URLの生成のための登録だけ。終端より後ろのため実行されない)。
   捕まえたパイプラインの先頭は Hanga の `IStartupFilter` が加える(中核機能と同じ)。
6. ホストを起動し(`StartAsync`)、続けて Chromium を起動する(`WarmUpAsync`。要件1.3)。失敗した場合はホストを停止・破棄してから例外を投げる。

- ルーティング(`UseRouting`・`UseEndpoints`)は、タグヘルパーの URL の生成(`asp-controller`/`asp-action`、`Url.Action`)のためだけに使う(検証レポート「6.4」)。
  属性で経路を指定したコントローラーと、既定の経路(`{controller=Home}/{action=Index}/{id?}`)のコントローラーの URL を生成できる(テストで確認)。
  Webアプリの `Startup` で独自に定義した経路は再現しない。バッチにコントローラーが無い場合、生成される URL は空になると考えられる(未確認。帳票では通常使わない)。
- **コントローラーのアクションは実行させない**: バッチのパイプラインには認証・認可が無く、Webアプリのグローバルな認可フィルターも入らない。
  帳票のページ(モデルの値を `Html.Raw` で出す場合など)から、Webアプリや帳票ライブラリのアクションが認証なしで実行されないよう、
  ルーティングの後ろに 404 を返す終端を置く(セキュリティレビューの指摘。テストで確認)。
- **コンテンツのルートを公開しない**: `WebRootPath`・`StaticFileMappings` に、コンテンツのルートそのもの、またはその上位のフォルダを指定すると、
  バッチの設定ファイル(`appsettings.json`)やプログラムが帳票のページから読めてしまうため、`StartAsync` で `HangaConfigurationException` にする(セキュリティレビューの指摘)。
- 環境名は `Production` に固定する。レイアウトの `<environment>` タグヘルパーは本番と同じ側が選ばれる。
- ホストの設定ファイル(`appsettings.json`)は読まない(Hanga の設定は `HangaOptions` で受け取る)。ログの出力先は `ConfigureServices` で登録する。
  Web のホストは `ASPNETCORE_` で始まる環境変数を読むため、コンテンツのルート・Webルート・環境名を明示して上書きし、
  外部のアセンブリによる起動時の処理(Hosting Startup)を読み込ませない(コードレビューの指摘)。
- 汎用ホストの既定の寿命の管理(`ConsoleLifetime`)は使わず、何もしないもの(`BatchHostLifetime`)に替える。
  `ConsoleLifetime` は Ctrl+C を握りつぶし、プロセスの終了時(`Environment.Exit` を含む)にホストの破棄を待って止まるため、バッチの終了を妨げる(コードレビューの指摘)。
- 呼び出し元から受け取った `HangaOptions` は、写さずにそのまま使う(`AddHanga` と違い、呼び出し元が作ったものを受け取るため)。起動後に値を変えないことを API の説明に書く。

### 帳票1件用の `HttpContext`(要件2.4, 3.4)

`Cshtml2Pdf` のバッチ用のコンストラクターは、`HangaBatch` と引数を保持するだけにする。`GenerateAsync` の中で次を行い、終わったらスコープを破棄する。

1. ホストのサービスから依存性注入のスコープを作る。生成が終わったら非同期で破棄する
   (`IAsyncDisposable` だけを実装したサービスを同期で破棄すると例外になるため。Webアプリの要求のスコープと同じ扱い。コードレビューの指摘)。
2. `DefaultHttpContext` を作り、`RequestServices` にスコープを設定する。スキームとホストは仮想オリジン(`HangaOptions.VirtualOrigin`)の値にする。
   ビューが絶対URLを生成した場合も、仮想オリジン(= 帳票のページのオリジン)を指すため、Chromium からの要求はパイプラインに渡る。
3. ダミーのエンドポイントを設定する(`SetEndpoint`)。タグヘルパーの URL の生成がエンドポイントルーティングの仕組みを使うようにするため(検証レポート「6.4」)。
4. `RequestSnapshot.From(httpContext)` で写し取る。Cookie・`Accept-Language` は空になり、パイプラインへの転送で付かない(要件3.4)。
5. 中核機能の `HangaPdfConverter.GenerateAsync` を呼ぶ(ビューのHTML化・ページの表示・PDF化)。

`HttpContext.User` は認証されていない空のユーザー、`Session` は使えない(使うと `HangaViewRenderingException`)。
`IHttpContextAccessor.HttpContext` は設定しない(null)。ビューが使う値はモデルから渡す(要件「対象外」)。
取り消しは、呼び出し元が渡した `CancellationToken` だけを使う(元の要求の取り消しが無いため)。

### ファイルへの保存(`Cshtml2Pdf.SaveAsync`。要件4.2)

1. PDF を生成する(失敗した場合は何も作らない。既存の動作)。
2. 保存先と同じフォルダに、一時ファイル(`<保存先のファイル名>.<ランダムな8文字>.tmp`)を新規に作って(`FileMode.CreateNew`。同じ名前のファイル・リンクがあれば上書きせず失敗)書き込む。
3. `File.Move(一時ファイル, 保存先, overwrite: true)` で保存先の名前に変える。同じフォルダ内の名前の変更のため、途中までのPDFが保存先に見えることは無い。
4. 2・3 で例外(取り消しを含む)が起きた場合は、一時ファイルを削除してから例外を投げ直す。既にある保存先のファイルは変わらない。
   書き込み・名前の変更の失敗は `IOException` などのまま投げる(Hangaの例外に包まない。要件4.3)。

バッチのプロセスが強制終了された場合は、一時ファイル(帳票の内容を含む)が保存先のフォルダに残ることがある。
既存のファイルを置き換えた場合、そのファイルに個別に付けたアクセス権は引き継がれず、フォルダから継承したものになる。どちらも利用の手引きに書く。

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
  - 帳票の生成の合間に Chromium のプロセスが終了していても、次の帳票は生成できること
  - ビューがセッションを使うと `HangaViewRenderingException` になること
  - `SaveAsync`: 失敗・取り消しで保存先にファイルができず、既存のファイルが壊れず、一時ファイルが残らないこと
  - 設定の誤り(フォルダが無い、Chromium が無い)が `StartAsync` で例外になること、終了後の利用が `ObjectDisposedException` になること
  - 処理時間(1件ずつ・並行)の計測
  - 外部への読み込みの遮断と厳格な扱い、ストリームへの書き込み、Chromium の使い回し、表示の待機中の取り消し、一時ユーザーデータフォルダの削除
  - コントローラーのアクションを実行させないこと(既定の経路・属性の経路の URL は生成できること)、帳票1件用の要求のスキーム・ホストと認証情報が無いこと
- 開発中の実行(静的Webアセットの一覧)、発行したバッチの動作、`ViewAssemblies` を指定しない場合の自動のビューの発見は、
  検証コードで .NET 5 のランタイムで確かめた(テストは testhost の下で動き、エントリのアセンブリがバッチと異なるため扱わない)。

## 検証結果(設計の根拠)

2026年10月4日、この開発環境(Linux)で、`spikes/batch-rcl-verification/` を使って確かめた。詳細は検証レポート「6.5」。

| バッチの SDK | 実行のしかた | ビューの発見 | `/_content/Reports/css/report.css` |
|---|---|---|---|
| `Microsoft.NET.Sdk` | ビルドの出力 | 自動では見つからない。アセンブリを指定すれば見つかる | 404(静的Webアセットの一覧が作られない) |
| `Microsoft.NET.Sdk` | 発行 | 同上 | 404(`wwwroot/css/report.css` に置かれ、`_content` の下に無い) |
| `Microsoft.NET.Sdk.Web` | ビルドの出力、.NET 5.0.17 のランタイム(自己完結型) | 自動で見つかる | 200(静的Webアセットの一覧 `BatchWeb.StaticWebAssets.xml` から) |
| `Microsoft.NET.Sdk.Web` | ビルドの出力、.NET 10 のランタイム(`RollForward`) | 自動で見つかる | 404(.NET 10 は新しい形式の一覧しか読まない。テスト環境だけの事情) |
| `Microsoft.NET.Sdk.Web` | 発行 | 自動で見つかる | 200(`wwwroot/_content/Reports/css/report.css`)。`asp-append-version` も付いた |

- 帳票ライブラリのビューは、.NET 5 の Razor SDK では別のアセンブリ(`Reports.Views.dll`)にコンパイルされる。アセンブリを指定する場合は、関連するビューのアセンブリも加える必要があった。
- 処理時間の目安は、下の「処理時間の目安」を参照(要件5.3)。

### 処理時間の目安(要件5.3)

2026年10月4日、この開発環境(CPU 4コア、Linux、Chrome 141)で、テスト用の帳票ライブラリの帳票(レイアウト・部分ビュー・CSS・JavaScript)を
`HangaBatch` で生成して計測した(`tests/Hanga.Tests/BatchLifetimeTests.cs` の「帳票1件あたりの時間」)。`MaxConcurrentRenders` は既定の4。

| 場合 | 時間 |
|---|---|
| 起動(ホストの起動と Chromium の起動) | 約0.7秒 |
| 初回の帳票(ビューの初回の読み込みを含む) | 約1.6秒 |
| 1件ずつ順に8件 | 約9.6秒(約1.2秒/件) |
| 8件を並行 | 約3.1秒(約0.4秒/件) |

中核機能と同じく、1件の時間の多くは「ネットワークの静止」の待ち時間(500ミリ秒)で、CPUをあまり使わない。
このため、バッチでも帳票を並行して生成すると、1件ずつより約3倍速くなった。上限は `MaxConcurrentRenders` で、メモリに合わせて決める(中核機能の design.md「同時に処理する帳票の数」)。

## 未検証の事項(Windows の検証環境で確認する)

- Windows 上の .NET 5 ランタイムでの、発行したバッチの動作(静的Webアセットの配置を含む)。
- Visual Studio 2019 で作った帳票ライブラリ・バッチのプロジェクトでの動作(.NET 5 SDK の Razor SDK)。
- タスクスケジューラから、バッチ用のユーザーで起動した場合の Chromium の起動(必要な起動引数)。
