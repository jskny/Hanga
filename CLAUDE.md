# Hanga

CSHTML(Razor)テンプレートとデータから、帳票PDFを簡易的に生成する .NET 5 / C# 向けライブラリ。
詳細は `.kiro/steering/product.md` を参照。

## 現在の状態

中核機能(`.kiro/specs/view-pdf-generation/`)と、バッチでの一括生成(`.kiro/specs/batch-pdf-generation/`)の実装は完了している
(Windows の検証環境での確認 = それぞれタスク14・タスク7を除く)。`dotnet build` / `dotnet test` / `dotnet format` はいずれもグリーン。

- `src/` — Core / Templating / Rendering / Hanga(公開API)。構成は `.kiro/steering/structure.md`
- `tests/` — ユニットテストと結合テスト(Chromium を使う。この開発環境では `/opt/pw-browsers` の Chromium を自動で使う。CI では `HANGA_TEST_CHROMIUM`)。
  `Hanga.TestApp` は呼び出し元アプリの代わり、`Hanga.TestReports` はバッチのテストで使う帳票ライブラリ(Razor クラスライブラリ)
- `samples/Hanga.Sample`・`tools/Hanga.ChromiumCheck` — サンプルアプリと Chromium 更新前の検証ツール
- `samples/Hanga.EdgeCases` — エッジケースの検証(不具合が起きやすい帳票・使い方を集めたもの。機能を追加・変更したら実行し、期待と違う結果が無いことを確かめる)
- `.kiro/steering/` — 常時適用される方針(製品概要・技術方針・プロジェクト構成)
- `.kiro/specs/view-pdf-generation/` — 中核機能(PDF用アクションからの生成)の要件定義書・設計書・実装タスクリスト
- `.kiro/specs/batch-pdf-generation/` — バッチ(Webアプリとは別の実行ファイル)での一括生成の要件定義書・設計書・実装タスクリスト
- `docs/ライブラリの使い方.md` — 呼び出し元アプリの開発者向けの手引き(登録・PDF用アクション・オプション・警告・例外・外字・バッチでの一括生成)
- `docs/Chromium更新前の検証手順.md` — 運用部門向けの検証手順
- `docs/PDF出力方式検証レポート.md` — PuppeteerSharp とテンプレート展開方式の検証結果(版のずれへの耐性、.NET 5 / VS2019 への対応状況、RazorLight と ASP.NET Core MVC の比較、バッチ、外字、非同期の値の取得、同時実行)
- `spikes/pdf-output-verification/`・`spikes/batch-rcl-verification/` — 上記の検証コード(製品コードではない。`Hanga.sln` に含めない)
- `docs/開発環境メモ.md` — Claude Code on the web実行環境の注意点(.NET 5のビルド方法、日本語フォント、Chromium、テスト用ホストの注意、`pkill -f`の自己マッチ問題など)

**方針と未決事項**(`.kiro/steering/tech.md`「採用技術」):

- PDF出力はヘッドレスChromiumで行い、**`PuppeteerSharp` は 18.1.0 に固定**する(20以降は .NET 5 非対応の依存を連れてくるため)。
  `GoToAsync`(`Page.navigate`)は使わない(18.1.0は新しいChromeで失敗する)。ページ内のJavaScriptで仮想オリジンへ移動し、
  そこへの要求(静的ファイル・API)はすべてアプリ自身のパイプラインにプロセス内で渡す(オペレーターのCookieを引き継ぐ)。根拠は `docs/PDF出力方式検証レポート.md`。
- テンプレート展開は ASP.NET Core MVC のビュー描画機能(`IRazorViewEngine`)を使い、RazorLight は採用しない(決定)。
  使い方はコントローラーのPDF用アクションからの生成と、Webアプリとは別の実行ファイル(バッチ)での一括生成の2つ。
  バッチでは、HangaがWebサーバーを持たないASP.NET Coreのホストをバッチの中に作り、Razor クラスライブラリにまとめた帳票のビューを使う(アプリケーションサーバーにはアクセスしない)。
  帳票テンプレートは呼び出し元アプリの既存ビューと同じ書き方(`ViewData`、`_Layout.cshtml`、`~/`、タグヘルパー)であり、RazorLight では扱えなかったため。
- 外字は、Hangaが外字用フォント(インストール済みのIPAmj明朝などを名前で参照)を注入して対応する。
- 利用部門の回答で決まった要件の前提は `.kiro/steering/product.md`「要件の前提」(バッチでの一括生成の前提を含む)。
- 本番サーバーは Windows Server(Docker不可)。Chromiumは運用部門がバージョンアップする前提で設計する。

実装中に環境起因と思われるエラーに遭遇したら、まず `docs/開発環境メモ.md` を確認する。

## 姉妹プロダクト Utsushi

Excel帳票をPDF化する姉妹ライブラリ Utsushi(`jskny/Utsushi`)が、同じ呼び出し元プロダクト・同じ実行環境・同じ制約で開発されている。
ドキュメント構成・ビルド設定(`Directory.Build.props`、CI)・開発の進め方はUtsushiを参考にしてよい。
ただし両者は独立したライブラリであり、互いに参照しない。Utsushiの記載を持ち込む場合は、Hangaに当てはまるかを確認してから採用する。

## 開発の進め方(spec駆動)

このプロジェクトはAWS Kiroスタイルのspec駆動開発に従う。

1. 新機能・大きな変更に着手する前に、`.kiro/specs/<機能名>/` に `requirements.md`(EARS形式の受け入れ基準)→ `design.md`(アーキテクチャ・データモデル)→ `tasks.md`(要件番号を参照した実装タスク)の順で仕様を作成・更新する。
2. 仕様に書かれた要件・設計から逸脱する実装をする場合は、先に仕様側を更新する。
3. `.kiro/steering/` の3ファイル(product.md / tech.md / structure.md)はプロジェクト全体に常時適用される方針であり、個別のspecより優先する。

## 必ず守る制約

- **.NET 5(`net5.0`)固定・C# 9.0まで**(呼び出し元プロダクトとその開発環境 Visual Studio 2019 の制約。`.kiro/steering/tech.md`)。file-scoped namespace・`record struct` など C# 10 以降の構文は使わない。ソリューションはclassic形式の `Hanga.sln` だけとし、`.slnx` は置かない。
- **商用ライブラリ禁止・Office Interop禁止**(`.kiro/steering/tech.md`)。新規依存ライブラリを追加する前に、ライセンス(同梱・ダウンロードされるバイナリを含む)と `net5.0` での動作を確認する。
- Razorテンプレートは任意のC#コードを実行できる。信頼できない第三者が書いたテンプレートを実行する機能は追加しない(`.kiro/steering/product.md`「非対応」)。
- レイヤー間の依存は一方向(`Core → Templating → Rendering → Hanga`)。上位レイヤーが下位レイヤーの実装詳細(依存ライブラリの型)を直接参照しない。依存ライブラリの型を公開APIに露出させない。
- 帳票固有の分岐はテンプレート・モデルの側に置き、ライブラリのコードにハードコードしない。

## サブエージェント

以下は `.claude/agents/` に定義済み。該当する状況でプロアクティブに使用する。

| サブエージェント | 使うタイミング |
|---|---|
| `code-investigator` | 実装に着手する前の既存コード調査、バグ調査時の関連コードの特定、影響範囲の洗い出し |
| `code-reviewer` | 実装の変更が完了した直後、コミットの前 |
| `test-writer` | 新しい実装・変更した実装にテストが不足している場合 |
| `security-reviewer` | 要求の転送・認証情報・Chromiumの起動・外部への通信・新しい依存パッケージを扱う変更の後 |
| `spec-compliance-reviewer` | requirements/design/tasks の作成・更新時、実装の完了後の仕様整合性の確認 |
| `doc-reviewer` | `.kiro/`・`docs/`・`README.md`・`CLAUDE.md` の追加・更新の直後 |

## 開発コマンド

リポジトリのルートで以下を実行する。

```bash
dotnet build
dotnet test
dotnet format

# 呼び出し元の開発環境(Visual Studio 2019)と同じC#コンパイラ(Roslyn 3.11)でビルドできるか確かめる
dotnet build --no-incremental -p:HangaVs2019Compiler=true

# エッジケースの検証(終了コード = 期待と違う結果の件数)
dotnet run --project samples/Hanga.EdgeCases -- --chromium <Chromium> --arg --no-sandbox --gaiji-font IPAmj明朝

# Chromium 更新前の検証ツール(サンプルの帳票の基準を作る/比べる)
dotnet run --project tools/Hanga.ChromiumCheck -- baseline --chromium <Chromium> --arg --no-sandbox --gaiji-font IPAmj明朝 --out <フォルダ>
```

この開発環境には `dotnet` がプリインストールされていない。セットアップ手順は `docs/開発環境メモ.md`「1. .NET SDK」を参照。
結合テストの外字のテストには IPAmj明朝(`apt-get install -y fonts-ipamj-mincho`)が必要。
