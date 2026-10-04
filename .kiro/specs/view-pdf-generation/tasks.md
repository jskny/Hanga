# 実装計画: ASP.NET Core MVC のビューからのPDF生成

対象要件: `.kiro/specs/view-pdf-generation/requirements.md`
対象設計: `.kiro/specs/view-pdf-generation/design.md`

> **状況**: 未着手(2026-10-04 作成)。
>
> **進め方**:
> - 上から順に進める。各タスクは、1回の作業(実装・テスト・レビュー・コミット)で終えられる大きさにしてある。作業してみて大きすぎると分かった場合は、着手前に分割してからこのファイルを更新する。
> - 各タスクの完了時に `dotnet build` / `dotnet test` / `dotnet format` をグリーンにし、チェックを付けてコミットする。
> - タスクの文面どおりに実現できなかった場合や、実装中に決めた事項は、各タスクに注記を付け、`design.md` にも反映する(要件・設計からの逸脱は先に仕様側を更新する。`CLAUDE.md`)。
> - 「(Windows)」が付いたタスクは、この開発環境(Linux)では確認できない。利用部門の検証環境で確認する。

- [x] 1. 開発基盤
  - [x] 1.1 ルートに classic形式の `Hanga.sln`、`Directory.Build.props`(`net5.0`・`LangVersion 9.0`・`Nullable`・EOL警告の抑止・`InternalsVisibleTo`)を作る
    - `.slnx` を作らないこと、ヘッダーが `# Visual Studio Version 16` であることを確かめる(`structure.md`)
    - _Requirements: 12.1_
  - [x] 1.2 `Directory.Build.props` に、VS2019 と同じコンパイラ(`Microsoft.Net.Compilers.Toolset` 3.11.0)へ差し替えるスイッチ(`-p:HangaVs2019Compiler=true`)を加える(Utsushi と同じ仕組み)
    - _Requirements: 12.1_
    - 注記: Web SDK(テスト用アプリ)が加えるアナライザーには `RunAnalyzers=false` が効かず、Roslyn 3.11 で警告 CS8032 が大量に出たため、
      検証モードではアナライザーの項目を外すターゲットを `Directory.Build.targets` に加えた。
  - [x] 1.3 テストプロジェクト共通の設定(`RollForward`、net5.0世代のテストパッケージ)を `Directory.Build.targets` にまとめる
    - _Requirements: 12.1_
  - [x] 1.4 プロジェクト `Hanga.Core`・`Hanga.Templating`・`Hanga.Rendering`・`Hanga` と、各テストプロジェクトを作り、参照の向きを `Core → Templating → Rendering → Hanga` にする
    - `Hanga.Rendering` は ASP.NET Core を参照しないこと(`design.md`「プロジェクト構成とレイヤー」)
    - _Requirements: 12.1_
  - [x] 1.5 `Hanga.Rendering` に `PuppeteerSharp` 18.1.0 を追加し、ビルド時に「`doesn't support net5.0`」の警告が出ないことを確かめる。依存パッケージ(`Newtonsoft.Json` 13.0.1 等)のライセンスを `tech.md` に記録する
    - _Requirements: 12.2, 12.3_
  - [x] 1.6 テスト用の ASP.NET Core 5 MVC アプリ `tests/Hanga.TestApp` を作る(ビュー・レイアウト・`_ViewStart`・静的ファイル・Cookie認証のログイン・認証が必要なAPI)。テストからはテスト用のホストで起動する
    - 検証コード `spikes/pdf-output-verification/inproc/` を元にする
    - _Requirements: 1, 3(以降のタスクのテストで使う)_
  - [x] 1.7 CI(GitHub Actions)を作る: Linux で build/test/format と VS2019 コンパイラでのビルド、Windows で .NET 5 SDK 5.0.408 でのビルド。結合テスト用に Chromium の場所を環境変数 `HANGA_TEST_CHROMIUM` で渡す
    - _Requirements: 12.1_
    - 注記: この開発環境では実行できない。初回の実行結果(Actions のログ)で成否を確かめる。
  - [x] 1.8 `.claude/agents/` に、Utsushi のサブエージェント定義(code-investigator・code-reviewer・test-writer・security-reviewer・spec-compliance-reviewer・doc-reviewer)を Hanga 向けに書き換えて置き、`CLAUDE.md` の「サブエージェント」を更新する

- [x] 2. Core: 共通の型
  - [x] 2.1 例外の基底 `HangaException`(段階 `Stage`)と派生7種(`design.md`「例外と警告」)を作る
    - _Requirements: 8.1, 8.2_
  - [x] 2.2 警告 `HangaWarning`(`Kind`・`Message`・`Detail`)を作る
    - _Requirements: 8.6_
  - [x] 2.3 用紙サイズ `PaperSize`(A3・A4・A5・B4・B5・Letter・Legal・`Custom(幅mm, 高さmm)`)、`PageOrientation`、`PageMargins`、`CssMedia` を作る
    - _Requirements: 5.1, 5.2_
  - [x] 2.4 アプリ全体の設定 `HangaOptions` を、`design.md` の既定値どおりに作り、値の検証(範囲外は `HangaConfigurationException`)を実装する
    - _Requirements: 4.3, 8.7, 9.4, 12.6_
    - 注記: 応答の大きさの上限 `MaxResponseBodyBytes`(既定 50MB)もオプションにした(design.md のオプション表に追記)。
  - [x] 2.5 帳票1件ごとの設定 `Cshtml2PdfOptions` を作り、`Timeout`・`Strict` をアプリ全体の設定で補う処理(実際に使う値を決める処理)を実装する
    - _Requirements: 4.3, 5, 8.7_
  - [x] 2.6 2.1〜2.5 のユニットテスト

- [x] 3. Templating: ビューのHTML化
  - [x] 3.1 `ViewHtmlRenderer`: `ActionContext` を作り、`IRazorViewEngine.FindView` でビューを探し、`StringWriter` に描画する基本の処理
    - _Requirements: 1.1, 1.2_
  - [x] 3.2 コントローラー名の既定(元の要求のコントローラー)と明示指定、コントローラーの `ViewData`(`ViewBag`)の引き継ぎ
    - _Requirements: 1.3_
  - [x] 3.3 ビューが見つからない場合(探した場所の一覧付き)と、描画中の例外の包み込み
    - _Requirements: 1.4, 1.5_
  - [x] 3.4 ビューのHTML化に必要な情報(`HttpContext`・ルーティング情報)を、引数として受け取る形にしておく(元の要求が無い場合の作り方はバッチの版で実装する)
    - _Requirements: 1.6_
  - [x] 3.5 `Hanga.TestApp` を使ったテスト: レイアウト・`ViewData`・セクション・部分ビュー・`~/`・`asp-append-version`・`asp-controller`/`asp-action` が画面と同じHTMLになること、見つからない場合・例外の場合
    - _Requirements: 1.1〜1.5_

- [x] 4. Rendering: Chromium の管理
  - [x] 4.1 `BrowserHost`: 設定された実行ファイルでChromiumを起動する(追加の引数、プロセスごとの一時ユーザーデータフォルダとその削除)。起動の失敗は `HangaBrowserException`(実行ファイルの場所付き)
    - _Requirements: 2.2, 8.3_
  - [x] 4.2 `BrowserHost`: 最初の利用時の起動(排他制御付き)と、起動直後の版の記録(`ILogger`)
    - _Requirements: 10.2, 11.3_
  - [x] 4.3 `BrowserHost`: Chromiumのプロセスの終了(`Disconnected`)を検知し、次の利用時に排他制御のうえで1回だけ起動し直す
    - _Requirements: 9.5_
  - [x] 4.4 同時実行数の枠(`SemaphoreSlim(MaxConcurrentRenders)`)と、帳票1件ごとのブラウザコンテキスト(`CreateBrowserContextAsync`)の作成・破棄(例外・取り消しの場合も破棄する)
    - _Requirements: 9.2, 9.4, 9.6, 10.1_
  - [x] 4.5 4.1〜4.4 のテスト(Chromiumを使う): 起動・版の取得、プロセスを止めた後の再起動、枠を超えた要求が待たされること、取り消し、コンテキスト間で localStorage が共有されないこと
    - _Requirements: 9.2, 9.4〜9.6_

- [x] 5. Rendering: ページの表示と要求の振り分け
  - [x] 5.1 `IVirtualOriginHandler`(仮想オリジンへの要求を受け取って応答を返すインターフェース)と、その要求・応答の型を定義する(PuppeteerSharp・ASP.NET Core の型を含めない)
    - _Requirements: 2.6, 3.1_
  - [x] 5.2 `ReportPage`: 要求への介入を有効にし、ページ内のJavaScript(`location.href`)で仮想オリジンへ移動し、`/__hanga/report` に帳票のHTMLを返す
    - _Requirements: 2.3, 2.4_
  - [x] 5.3 要求の振り分け(1): `/__hanga/` のリソース(埋め込みリソース)、`favicon.ico`(204)、その他の仮想オリジンへの要求を `IVirtualOriginHandler` へ渡す
    - _Requirements: 3.1, 3.7_
  - [x] 5.4 要求の振り分け(2): 仮想オリジンへの要求の失敗(400以上・300番台)の記録と、表示の後のエラー化(`HangaResourceRequestException`)
    - _Requirements: 3.5, 8.4_
    - 注記: 失敗の記録は `ReportPage` で行う(タスク5で実装済み)。エラー化は、描画全体を組み立てる `ReportRenderer`(タスク6.3と同時)で行う。
  - [x] 5.5 要求の振り分け(3): 許可した外部ホストは通し、それ以外は遮断して警告 `BlockedExternalRequest`(厳格ならエラー)
    - _Requirements: 3.4, 3.6_
  - [x] 5.6 5.1〜5.5 のテスト(Chromiumを使う。`IVirtualOriginHandler` はテスト用の実装を使う)
    - _Requirements: 2.3, 2.4, 3.1, 3.4〜3.7_

- [x] 6. Rendering: 表示の完了の待機
  - [x] 6.1 ネットワークの静止(`Networkidle0`)と `document.fonts.ready` を待つ
    - _Requirements: 4.1_
  - [x] 6.2 完了条件の式(`ReadyExpression`)を待つ
    - _Requirements: 4.2_
  - [x] 6.3 全体の上限時間と、超えた場合の `HangaTimeoutException`(待っていた条件と、未完了の要求のURL付き)
    - _Requirements: 4.3, 4.4_
  - [x] 6.4 ページの `PageError` を警告 `ScriptError` にする(厳格ならエラー)
    - _Requirements: 4.5_
  - [x] 6.5 6.1〜6.4 のテスト(Chromiumを使う): 遅れて描画するJavaScript、完了条件の式、上限時間の超過、スクリプトの例外
    - _Requirements: 4.1〜4.5_

- [x] 7. Rendering: 体裁とPDF化
  - [x] 7.1 `PdfLayout`(Chromium不要の計算部分): 用紙の寸法(mm)と向き、幅を収める倍率、1ページの高さの計算。ユニットテスト付き
    - _Requirements: 5.1〜5.5_
  - [x] 7.2 印刷用/画面用CSSの切り替え(`EmulateMediaTypeAsync`)と、幅・高さの測定(ページ内で `scrollWidth`/`scrollHeight` を測る)
    - _Requirements: 5.4〜5.6_
    - 注記: 内容の幅は、画面の幅を印刷可能な幅にしてから測る(伸び縮みする画面を不要に縮小しないため)。倍率の式も、指定の倍率を掛けた後にはみ出す場合に収まるよう改めた(design.md「⑨」に反映)。
  - [x] 7.3 PDFの出力(`PdfDataAsync`): 寸法(mm)・余白・倍率・背景・ページ番号のフッター・文書のタイトル
    - _Requirements: 2.1, 2.5, 5.1〜5.3, 5.7〜5.9_
  - [x] 7.4 7.2〜7.3 のテスト(Chromiumを使う): ページサイズ(±1pt)、横向き、印刷用CSS、幅の広い表が欠けないこと、1ページ化、ページ番号、タイトル、文字列を取り出せること
    - _Requirements: 2.5, 5.1〜5.9_

- [x] 8. Rendering: 外字・異体字・字形の無い文字
  - [x] 8.1 外字用フォントの注入: `@font-face`(`local()` または `/__hanga/gaiji` のファイル)と `unicode-range`、全要素の `font-family` の末尾への追加。表示の完了の後に実行する
    - _Requirements: 6.1〜6.3, 6.5, 10.3_
  - [x] 8.2 異体字セレクタ付きの文字の包み込み(`HangaGaijiIvs`)
    - _Requirements: 6.4_
  - [x] 8.3 判定用フォントの生成スクリプトを `tools/probe-fonts/` に置き、生成物を `Hanga.Rendering` の埋め込みリソースにする(`spikes/.../design-checks/make_probe_fonts.py` を元にする)
    - _Requirements: 6.7_
  - [x] 8.4 字形の無い文字の検出(判定用フォントによる描き比べ)と、警告 `MissingGlyph`(符号位置付き。厳格ならエラー)
    - _Requirements: 6.7_
  - [x] 8.5 外字用フォントが無い場合のエラー: ファイルの指定は登録時、名前の指定はChromiumの起動時に確かめる
    - _Requirements: 6.6_
  - [x] 8.6 8.1〜8.5 のテスト(Chromiumを使う。IPAmj明朝が必要): 「𠮷」が外字用フォントで描かれること、「葛+U+E0102」が異なる字形になること、字形の無い文字の検出、フォントが無い場合のエラー
    - _Requirements: 6.1〜6.7_

- [x] 9. Hanga: ASP.NET Core との接続
  - [x] 9.1 `AddHanga`(コードで指定・設定ファイルから読む・両方)と、登録時の値の検証
    - _Requirements: 12.5, 12.6_
  - [x] 9.2 パイプラインを捕まえる `IStartupFilter` と、保持するシングルトン
    - _Requirements: 3.1, 12.5_
  - [x] 9.3 `RequestSnapshot`: 元の要求から `Cookie`・`Host`・スキーム・パスベース・`Accept-Language`・`IServiceScopeFactory` を写し取る
    - _Requirements: 3.2, 3.3, 9.3_
  - [x] 9.4 `PipelineForwarder`(`IVirtualOriginHandler` の実装): 要求ごとのスコープと `DefaultHttpContext` を作り、パイプラインを呼んで応答を返す(応答の大きさの上限付き)
    - _Requirements: 3.1〜3.3, 3.8_
  - [x] 9.5 アプリの停止時にChromiumを終了し、`LaunchOnStartup` なら起動時にChromiumを起動する `IHostedService`
    - _Requirements: 10.2_
  - [x] 9.6 9.1〜9.5 のテスト(`Hanga.TestApp` を使う): 設定ファイルからの読み込みと不正な値、静的ファイルとAPIがパイプラインで処理されること、Cookie の引き継ぎ
    - _Requirements: 3.1〜3.3, 12.5, 12.6_

- [x] 10. Hanga: 公開API
  - [x] 10.1 `HangaPdfConverter`(共有の変換器): ビューのHTML化からPDF化までをつなぎ、`HangaPdfDocument`(PDF・警告・Chromiumの版・所要時間)を返す。警告を `ILogger` に記録する
    - _Requirements: 8.4, 8.6, 9.1_
  - [x] 10.2 `Cshtml2Pdf`: コンストラクター(コントローラーから / `HttpContext` から)、`Options`、`GenerateAsync`・`ToBytesAsync`・`WriteToAsync`・`SaveAsync`
    - _Requirements: 1.3, 7.1, 9.1_
  - [x] 10.3 `ToActionResultAsync`: `inline`/`attachment` と、日本語のファイル名(`filename*` と ASCII の代替名)
    - _Requirements: 7.2〜7.4_
  - [x] 10.4 例外のメッセージ・警告に `Cookie` などの値が入らないことの確認(全例外・全警告の組み立て箇所を見直す)
    - _Requirements: 8.5_
    - 注記: 失敗した要求の URL とログにクエリ文字列が残っていたため、例外・警告・ログに載せる URL はすべてクエリ文字列を除く形にそろえた(design.md「例外と警告」に反映)。
  - [x] 10.5 10.1〜10.4 のテスト: `Content-Disposition` の組み立て(ユニット)、PDF用アクションからの生成(`Hanga.TestApp`、Chromiumを使う)
    - _Requirements: 7, 8.5, 8.6_

- [x] 11. 全体の結合テスト(`Hanga.TestApp`、Chromiumを使う)
  - [x] 11.1 Cookie認証のAPIから表示時に取得した値がPDFに入ること
    - _Requirements: 3.2, 3.8_
  - [x] 11.2 ログインが切れた場合(APIがログイン画面へリダイレクト)にエラーになること
    - _Requirements: 3.5, 8.4_
    - 注記: Cookie 認証は要求の種類によって 302 ではなく 401 を返すことがある。どちらも失敗として扱うことを確かめた。
  - [x] 11.3 8人の同時要求で、各PDFに本人の値だけが入ること(`spikes/.../inproc/concurrent-test.sh` の内容をテストにする)
    - _Requirements: 9.1〜9.4_
  - [x] 11.4 厳格な扱いの切り替え(アプリ全体の設定と帳票ごとの上書き)
    - _Requirements: 8.7_
  - [x] 11.5 処理時間を計測し、`design.md` に1件あたりの目安として記録する
    - _Requirements: 10.4_

- [x] 12. Chromium のバージョンアップの検証ツール
  - [x] 12.1 サンプルアプリ `samples/Hanga.Sample`: サンプルの帳票ビュー(外部CSS・JavaScript・APIからの値の取得・外字・異体字・幅の広い表・複数ページ)とテスト用のログイン、PDF用アクション
    - _Requirements: 11.1_
  - [x] 12.2 PDFから文字列を取り出すライブラリの選定(候補 PdfPig)。ライセンス、`net5.0` での動作、.NET 5 非対応の依存を連れてこないことを確かめ、`tech.md` に記録する
    - _Requirements: 11.2, 12.2, 12.3_
    - 注記: PdfPig 0.1.16 を採用(タスク1で `tech.md` に記録済み)。
  - [x] 12.3 `tools/Hanga.ChromiumCheck`(1): 指定したChromiumでサンプルアプリを起動し(ローカルホストのみ)、各帳票のPDFとページの画面写真を集めて保存する(基準の作成)
    - _Requirements: 11.1, 11.3_
  - [x] 12.4 `tools/Hanga.ChromiumCheck`(2): 基準と比べ(ページ数・ページサイズ・文字列・画面写真)、違いの一覧を出す
    - _Requirements: 11.2_
    - 注記: 画面写真はアプリの画面そのものを撮るため、Hanga の処理(外字の組み込み・幅の縮小)を経た PDF の変化を捉えられなかった。
      画面写真をやめ、PDF の各文字の位置とフォントを比べる方式にした(design.md に反映)。Chrome 141 で作った基準と Chrome 154 を比べて違いが無いこと、
      外字用フォントを外すと違いを検出することを確かめた。
  - [x] 12.5 運用部門向けの手順書 `docs/Chromium更新前の検証手順.md`
    - _Requirements: 11.4_

- [ ] 13. ドキュメント
  - [ ] 13.1 `docs/ライブラリの使い方.md`: 登録(コード・設定ファイル)、PDF用アクションの書き方、オプション一覧、返し方、警告と厳格な扱い、例外、同時処理数の決め方、本番環境に必要なもの(Chromium・外字用フォント)、注意点(APIの値の取得は表示時のみ、URLのクエリに秘密情報を入れない)
  - [ ] 13.2 `README.md` を、使い方の最小例・特長・ライセンスを含む形に書き直す
  - [ ] 13.3 `.kiro/steering/`・`CLAUDE.md` を実際の構成に合わせて更新する

- [ ] 14. (Windows)利用部門の検証環境での確認
  - [ ] 14.1 Visual Studio 2019 でのビルドと、Windows 上の .NET 5 ランタイムでの動作
    - _Requirements: 12.1_
  - [ ] 14.2 IIS の配下(アプリケーションプールのユーザー)での Chromium の起動。必要な起動引数を `docs/ライブラリの使い方.md` に記録する
    - _Requirements: 12.4_
  - [ ] 14.3 判定用フォント(cmap format 13)を Windows の Chromium が読み込めるか。読み込めない場合は format 12 で作り直す
    - _Requirements: 6.7_
  - [ ] 14.4 インストール済みの IPAmj明朝 の名前での参照(`local()`)
    - _Requirements: 6.1_
  - [ ] 14.5 実際の帳票ビュー・レイアウト・APIでの動作(POST と Antiforgery のトークンを使う画面を含む)
    - _Requirements: 1, 3_
  - [ ] 14.6 同時処理数を変えたときのメモリと処理時間を計測し、本番の設定値を決める
    - _Requirements: 9.4_
  - [ ] 14.7 `tools/Hanga.ChromiumCheck` を、運用部門の手順書どおりに実行できること
    - _Requirements: 11_
