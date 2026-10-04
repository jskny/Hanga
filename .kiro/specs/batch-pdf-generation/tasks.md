# 実装計画: バッチでの帳票PDFの一括生成

対象要件: `.kiro/specs/batch-pdf-generation/requirements.md`
対象設計: `.kiro/specs/batch-pdf-generation/design.md`

> **進め方**: 中核機能(`.kiro/specs/view-pdf-generation/tasks.md`)と同じ。各タスクの完了時に `dotnet build` / `dotnet test` / `dotnet format` をグリーンにし、チェックを付けてコミットする。
> タスクの文面どおりに実現できなかった場合や、実装中に決めた事項は、各タスクに注記を付け、`design.md` にも反映する。
> 「(Windows)」が付いたタスクは、この開発環境(Linux)では確認できない。利用部門の検証環境で確認する。
>
> **状況**: タスク1〜6 完了(2026-10-04)。タスク7(Windows の検証環境での確認)は利用部門の環境で行う。
> code-reviewer・security-reviewer・doc-reviewer のレビューを行い、指摘(汎用ホストの既定の寿命の管理による Ctrl+C・終了の横取り、帳票ごとのスコープの非同期の破棄、
> 環境変数による動作の変化、バッチでのコントローラーのアクションの実行、コンテンツのルートの公開、一時ファイルの作り方など)に対応した(design.md に反映)。

- [x] 1. 方式の検証
  - [x] 1.1 別の実行ファイルから、Razor クラスライブラリのビューと静的Webアセットを、Webサーバーを起動せずに使えるかを確かめる(`spikes/batch-rcl-verification/`)
    - バッチの SDK(`Microsoft.NET.Sdk` / `Microsoft.NET.Sdk.Web`)と実行のしかた(ビルドの出力 / 発行、.NET 5 / .NET 10 のランタイム)の組み合わせ
    - _Requirements: 2.1, 2.2, 3.2_
  - [x] 1.2 結果を `docs/PDF出力方式検証レポート.md`「6.5」に記録する

- [x] 2. 仕様と方針の更新
  - [x] 2.1 `requirements.md`・`design.md`・`tasks.md`(本書)を作る
  - [x] 2.2 `.kiro/steering/`(product.md・tech.md・structure.md)の「将来の候補」の記載を、バッチが要件化されたことに合わせて更新する
  - [x] 2.3 中核機能の仕様(`view-pdf-generation`)の「対象外」「要件1.6」「設計 ②」の記載から、この仕様を参照する

- [x] 3. ファイルへの保存の改善
  - [x] 3.1 `Cshtml2Pdf.SaveAsync` を、同じフォルダの一時ファイルに書いてから名前を変える形にする。失敗・取り消しでは一時ファイルを削除する
    - _Requirements: 4.2_

- [x] 4. バッチ用の変換器
  - [x] 4.1 `HangaBatchOptions`(ビューのアセンブリ・コンテンツのルート・Webルート・URLのパスとフォルダの対応・サービスの追加登録)と、その検証
    - _Requirements: 2.2, 2.5, 3.3_
  - [x] 4.2 `NoopServer` と `BatchHostBuilder`: Webサーバーを持たないホストの組み立て(MVC・アプリケーションパーツ・静的Webアセット・静的ファイル・ルーティング・Hanga の部品の登録)
    - `AddHanga` の登録処理を、バッチのホストからも使える形にする(Webアプリの動作は変えない)
    - _Requirements: 1.1, 1.2, 2.1, 2.2, 3.1〜3.3, 6.4_
    - 注記: レビューを受けて、寿命の管理を何もしないものに替え、環境名などを固定し、コントローラーのアクションを実行させない終端を加えた。
      URL の生成には既定の経路の登録(`MapDefaultControllerRoute`)も必要だった(`MapControllers` だけでは属性で経路を指定したコントローラーしか対象にならない)。
  - [x] 4.3 `HangaBatch`: `StartAsync`(ホストの起動と Chromium の起動。失敗時の後始末)、`ChromiumVersion`、`DisposeAsync`/`Dispose`、終了後の利用の検出
    - _Requirements: 1.3〜1.5_
  - [x] 4.4 `Cshtml2Pdf` のバッチ用のコンストラクターと、帳票1件用のスコープ・`HttpContext`(仮想オリジンのスキームとホスト・ダミーのエンドポイント)の作成
    - _Requirements: 1.5, 2.3, 2.4, 3.4_

- [x] 5. テスト
  - [x] 5.1 テスト用の帳票ライブラリ `tests/Hanga.TestReports`(Razor クラスライブラリ)を作り、`Hanga.sln` に加える
  - [x] 5.2 結合テスト: モデル・CSS・JavaScript・`@inject` の反映、`StaticFileMappings` と発行した形の `WebRootPath` の両方
    - _Requirements: 2.1〜2.5, 3.1〜3.3, 3.6_
  - [x] 5.3 結合テスト: 並行した生成で値が取り違えられないこと
    - _Requirements: 1.5, 5.2_
  - [x] 5.4 結合テスト: 失敗(ビューが無い・ビューの例外・静的ファイルが無い・Chromium の終了)の後も続けて生成できること、取り消し
    - _Requirements: 2.6, 3.5, 4.3〜4.5_
  - [x] 5.5 `SaveAsync` の失敗・取り消しで、保存先と一時ファイルが残らないこと、既存のファイルが壊れないこと
    - _Requirements: 4.2_
  - [x] 5.6 設定の誤り・Chromium の起動の失敗が `StartAsync` で例外になること、終了後の利用、ユニットテスト(設定の検証)
    - _Requirements: 1.3, 1.4, 3.3_
  - [x] 5.7 処理時間(1件ずつ・並行)を計測し、`design.md` に記録する
    - _Requirements: 5.3_

- [x] 6. ドキュメント
  - [x] 6.1 `docs/ライブラリの使い方.md` に「バッチでの一括生成」を加える(帳票ライブラリとバッチの作り方、起動と終了、1件ごとの生成、並行数の抑え方、失敗の扱い、使えない情報)
    - _Requirements: 6.5_
  - [x] 6.2 `README.md`・`CLAUDE.md` を更新する

- [ ] 7. (Windows)利用部門の検証環境での確認
  - [ ] 7.1 Visual Studio 2019 で、帳票ライブラリ(Razor クラスライブラリ)とバッチ(`Microsoft.NET.Sdk.Web`)を作り、ビルド・発行できること
    - _Requirements: 6.1, 6.5_
  - [ ] 7.2 発行したバッチを Windows Server で実行し、静的Webアセットを含む帳票が PDF になること
    - _Requirements: 3.2, 6.2_
  - [ ] 7.3 タスクスケジューラから、バッチ用のユーザーで起動した場合の Chromium の起動(必要な起動引数を `docs/ライブラリの使い方.md` に記録する)
    - _Requirements: 6.2_
