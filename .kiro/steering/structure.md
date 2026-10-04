---
inclusion: always
---

# Hanga プロジェクト構成方針

> 中核機能(`.kiro/specs/view-pdf-generation/`)の実装は完了している。以降の変更も本方針に従うこと。

## レイヤー構成

帳票PDF生成のパイプラインは、以下の一方向の依存関係を持つレイヤーに分割する。

```
共通基盤 (Core)  ← 全レイヤーが参照してよい唯一の下位プロジェクト
        ↓
テンプレート展開レイヤー (Templating)    CSHTML + モデル → HTML
        ↓
PDF出力レイヤー (Rendering)            HTML → PDF(ヘッドレスChromium。外部リソースの解決を含む)
        ↓
ファサード (Hanga)  ← 呼び出し元プロダクトが参照する唯一のアセンブリ
```

- `Hanga.Core` は例外階層(`HangaException` とその派生)と、レイヤーをまたぐ値型(用紙サイズ・余白・単位など)のみを持つ。
  ロジックは置かず、どのレイヤーからも参照してよい。逆に `Hanga.Core` は他のどのプロジェクトも参照しない。
- 上位レイヤーは下位レイヤーの実装詳細(例: ASP.NET Core MVC のビュー描画の型、PuppeteerSharp の型)を直接知ってはならない。
  レイヤー間の受け渡しは、各レイヤーが定義するインターフェースと内部モデル(POCO)を介する。
- PDF出力レイヤーは `IPdfRenderer` のようなインターフェースの裏に実装を置き、出力方式を差し替えられるようにする。
  Chromiumの操作に使うライブラリ(PuppeteerSharp)の型は、このレイヤーの外に出さない(`tech.md`「Chromiumのバージョンアップへの備え」)。
- 外部リソース(スタイル・スクリプト・画像・フォント)の解決は、PDF出力レイヤーの責務とする。URLのパスとサーバー上のフォルダの対応付けは、ファサード経由で呼び出し元が設定する。
- 呼び出し元プロダクトに見せる公開APIはファサード `Hanga` に集約する。依存ライブラリの型を公開APIに露出させない。

## ソリューション構成

```
Hanga/
├── src/
│   ├── Hanga.Core/          # 例外階層・警告・用紙サイズなどの値型・オプション(HangaOptions / Cshtml2PdfOptions)
│   ├── Hanga.Templating/    # ビューのHTML化(ViewHtmlRenderer。ASP.NET Core MVC のビュー描画を使う)
│   ├── Hanga.Rendering/     # HTML→PDF(BrowserHost・ReportPage・ReportRenderer・PdfPrinter・GlyphSupport。PuppeteerSharp 18.1.0。ASP.NET Core に依存しない)
│   │   └── Fonts/           # 字形の確認に使う判定用フォント(埋め込みリソース。tools/probe-fonts で生成)
│   └── Hanga/               # 公開API(AddHanga・Cshtml2Pdf・HangaPdfConverter)と ASP.NET Core との接続(Hosting/)
├── tests/
│   ├── Hanga.<レイヤー名>.Tests/   # レイヤーごとのテスト(Rendering は Chromium を使う)
│   ├── Hanga.Tests/         # 公開API・ASP.NET Core との接続・全体の結合テスト
│   ├── Hanga.TestApp/       # テスト用の ASP.NET Core 5 MVC アプリ(呼び出し元アプリの代わり)
│   └── Hanga.TestSupport/   # テストの共通部品(Chromium の場所、PDF の読み取り)
├── samples/
│   └── Hanga.Sample/        # サンプルアプリ(使い方の例と、検証ツールの対象)
├── tools/
│   ├── Hanga.ChromiumCheck/ # Chromium 更新前の検証ツール
│   └── probe-fonts/         # 判定用フォントの生成スクリプト(Python + fontTools)
├── spikes/                  # 方式の検証コード(製品コードではない。Hanga.sln に含めない)
├── docs/                    # 人が読む補足ドキュメント
├── .kiro/
│   ├── steering/            # 本ファイル群。プロジェクト全体に常時適用される方針
│   └── specs/               # 機能ごとの要件定義書・設計書・タスクリスト
├── .claude/
│   └── agents/              # サブエージェント定義
├── Directory.Build.props    # 全プロジェクト共通のビルド設定(TFM/LangVersion/Nullable、VS2019コンパイラでの検証スイッチ)
├── Directory.Build.targets  # テストプロジェクト共通設定(RollForward/テストパッケージ)、VS2019コンパイラでの検証時のアナライザーの除外
└── Hanga.sln                # classic形式。Visual Studio 2019でもそのまま開ける
```

## 命名規則

- 名前空間・プロジェクト名は `Hanga.<レイヤー名>` とする。ファサードは `Hanga`。
- 型名が名前空間名と衝突する場合(例: 名前空間 `Hanga.Templating` と型 `Templating`)は、Utsushiの `Utsushi.ReportDefinitions` の例にならい、名前空間を複数形にする等で回避し、その理由を本ファイルに記録する。
- サンプル帳票は `samples/templates/<帳票コード>/` に帳票コード単位でディレクトリを分ける。

## 帳票の追加手順

ライブラリのコードは変更しない。呼び出し元アプリで次を行う(`docs/ライブラリの使い方.md`)。

1. 帳票のビュー(`.cshtml`)を作る(画面用のビューをそのまま使ってよい)。
2. コントローラーに PDF 用のアクションを追加し、`new Cshtml2Pdf(this, ビュー名, モデル)` で PDF を返す。
3. 用紙サイズ・向き・余白などを `pdf.Options` で指定する。
4. ライブラリの対応範囲で表現できない要件がある場合のみ、`.kiro/specs/` に要件を追記し、ライブラリを拡張する。

## ドキュメントの関係

- `.kiro/steering/`(本ファイル群): プロジェクト全体に常時適用される方針。個別のspecより優先する。
- `.kiro/specs/<機能名>/`: 機能ごとの `requirements.md`(EARS形式の受け入れ基準)→ `design.md`(アーキテクチャ・データモデル)→ `tasks.md`(要件番号を参照した実装タスク)。
- `docs/`: 人が読む補足ドキュメント(開発環境メモ、ライブラリの使い方、テンプレート作成ガイドなど)。
- `CLAUDE.md`: このリポジトリで作業するAIエージェント向けの指示。人間の開発者にも同様に適用される。
