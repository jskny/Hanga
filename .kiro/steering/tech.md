---
inclusion: always
---

# Hanga 技術方針

## 実行環境

- **言語/ランタイム**: C# / .NET 5
- **対象OS**: サーバーサイドでの実行を想定(Windows依存・COM依存を作らない。将来的なLinuxコンテナでの実行を妨げない設計とする)

> **.NET 5固定の理由**: 本ライブラリを利用する予定の呼び出し元プロダクトが .NET 5 上で動作しており、そのプロセスに読み込まれる(同一プロセス内でアセンブリとして参照される)ことを前提とするため、`net5.0` をターゲットフレームワークとして固定する。呼び出し元の .NET バージョンが上がらない限り、Hanga側だけを新しいTFMに上げることはできない。

> **Visual Studio 2019 対応**: 呼び出し元プロダクトの開発環境が Visual Studio 2019 であるため、Hanga側も VS2019 でビルドできる必要がある。VS2019(最終版16.11)がバンドルするC#コンパイラは **C# 9.0 までしかサポートしない**(C# 10はVS2022以降が必要)。TFMが`net5.0`であることとC#の言語バージョンは別軸のため、`LangVersion`を10以上にするとVS2019ではコンパイルエラーになる。このためLangVersionは`9.0`に固定し、file-scoped namespaceやrecord struct等C# 10以降の構文は使用しない。加えてVS2019は新しいXML形式のソリューションファイル(`.slnx`)を認識できないため、ソリューションファイルはルート直下のclassic形式の`Hanga.sln`だけとする(`structure.md`「ソリューション構成」参照)。
>
> - **.NET SDKのバージョン**: .NET SDK 6.0.300以降はVisual Studio 16.11以前には読み込まれない(Microsoft Learn「Version requirements for .NET 6 SDK」)。VS2019のPCで新しいSDKも入っている場合は、リポジトリのルートに `global.json` を置いて .NET 5 SDK(5.0.4xx)に固定する。この開発環境とCIのLinuxジョブには.NET 5 SDKが無いため、`global.json` はコミットしない(`.gitignore` で除外する)。手順は `docs/開発環境メモ.md`「1. .NET SDK」を参照。
> - **コンパイラの差**: 新しいSDKのコンパイラは `LangVersion=9.0` でも、細かな挙動がVS2019のコンパイラ(Roslyn 3.11)と異なりうる。Utsushiと同様に、VS2019 16.11と同じRoslyn 3.11(NuGetパッケージ `Microsoft.Net.Compilers.Toolset` 3.11.0、MIT)に差し替えてビルドするスイッチを `Directory.Build.props` に設け、CIで確認する。

> **注記(既知のリスク)**: .NET 5 は Microsoft のサポートが終了(EOL)しており、セキュリティパッチは提供されない。この点は呼び出し元プロダクトの制約に起因する既知のリスクとして許容し、呼び出し元が .NET 8 以降へ移行した際にはHanga側のTFMも追随できるよう、特定バージョンのランタイムAPIに過度に依存しない実装を心掛ける。
> 依存ライブラリについても、`net5.0` を直接ターゲットに含まない(`netstandard2.0` のみ等)パッケージは、`net5.0` 上で実際に動くことを確認してから採用する。

> **開発環境での .NET 5 SDKの扱い**: Claude Code on the webの実行環境には .NET 5 SDKが無いが、`dotnet-sdk-10.0` だけで `net5.0` のビルド・テストが行える(実行するプロジェクトには `<RollForward>LatestMajor</RollForward>` が必要)。手順・根拠は `docs/開発環境メモ.md`「1. .NET SDK」を参照。

## ライセンス制約

- **商用ライブラリ禁止**: 有償ライセンス、または商用利用時に課金が発生するライセンス(収益上限のあるCommunity版など)は採用しない。
- **Office Interop禁止**: COM相互運用機構は使用しない。
- 採用する依存ライブラリは **MIT / Apache-2.0 / BSD 等の永続的な無償OSSライセンス**に限定する。
- 依存ライブラリが同梱・ダウンロードするネイティブバイナリや実行ファイル(ブラウザ等)のライセンスも、同じ基準で確認する。
- 新規に依存ライブラリを追加する際は、ライセンス種別を確認し、本ファイルの採用技術の表を更新する。

## 処理の流れ

Hangaの処理は、次の2段階に分かれる。各段階は別のレイヤー(`structure.md`)の責務とし、段階間はHTML文字列(と付随する設定)で受け渡す。

```
CSHTMLテンプレート + モデル
        │  ① テンプレート展開(Razor)
        ▼
      HTML
        │  ② PDF出力(HTML → PDF)
        ▼
       PDF
```

②の方式によってHTML/CSSの対応範囲・実行環境への要求が大きく変わるため、②は差し替え可能なインターフェースの裏に置く。

## 採用技術(候補と選定状況)

2026年10月3日時点の NuGet の最新版の情報(パッケージの nuspec)で確認した。

### ① テンプレート展開(CSHTML → HTML)

| 候補 | 版 | ライセンス | 対象フレームワーク | 備考 |
|---|---|---|---|---|
| `RazorLight` | 2.3.1 | Apache-2.0 | netcoreapp3.1 / netstandard2.0 / **net5.0** / net6.0 | ASP.NET Core MVC無しでRazorテンプレートを文字列に展開できる。net5.0向けの依存は `Microsoft.AspNetCore.Mvc.Razor.Extensions` 5.0.0 等で揃っている。最終リリースが2023年1月で、更新が止まっている点に注意 |

ASP.NET Core MVCのビュー描画機能(`IRazorViewEngine`)を直接使う方法もあるが、呼び出し元がWebアプリとは限らないため、ライブラリ単体で完結する `RazorLight` を第一候補とする。

### ② PDF出力(HTML → PDF)【未決】

| 候補 | 版 | ライセンス | 対象フレームワーク | 方式・特徴 |
|---|---|---|---|---|
| `PuppeteerSharp` | 25.12.0 | MIT | netstandard2.0 / net8.0 / net10.0 | ヘッドレスChromiumを子プロセスとして操作し、Chromiumの印刷機能でPDF化する。CSSの再現度は最も高い。実行環境にChromiumが必要 |
| `Microsoft.Playwright` | 1.63.0 | MIT | netstandard2.0 | 方式は同上(Chromium)。加えてNode.jsベースのドライバを同梱する |
| `HtmlRenderer.PdfSharp` | 1.6.1 | BSD-3-Clause | netstandard2.0 / net8.0 | HTMLを自前で解釈して `PDFsharp`(MIT)で描画する。プロセス内で完結するが、対応するHTML/CSSはHTML 4/CSS 2程度に限られる。`System.Drawing.Common` に依存するため、Linuxでの動作は要検証 |
| 自前のレイアウト + `SkiaSharp` | (Utsushiは2.88.8) | MIT | netstandard2.0 ほか | Utsushiと同じ描画基盤。対応するHTML/CSSのサブセットを自分で決めて実装する。自由度は高いが実装量が最も多い |

除外したもの:
- `QuestPDF`: Community版に収益上限があり(一定以上の収益がある組織は有償)、ライセンス制約に該当する。またHTMLではなくC#のAPIでレイアウトを書く方式である。
- `wkhtmltopdf` 系(`DinkToPdf` 等): 本体の開発が終了しており、保守されていない。
- 商用のHTML→PDF変換製品全般。

#### 選定で確認すること

- 呼び出し元プロダクトの本番サーバーに、Chromium(と必要な共有ライブラリ・フォント)を配置できるか。できない場合、Chromium系の候補は使えない。
- 帳票テンプレートで使いたいCSS(表組み、罫線、`page-break-*`/`break-*` による改ページ、ページ番号、ヘッダー/フッター)の範囲。
- 1帳票あたりの生成時間・同時実行数の要件(Chromium系はプロセス起動・ページ生成のコストがある)。
- 日本語フォントの埋め込み方式と、PDF内の文字列検索・コピーができるか(`pdffonts` で `Type 3` にならないか)。
- netstandard2.0 のみをターゲットにするパッケージが、`net5.0` 上で実際に動くか。

選定の結果は本節に記録し、`.kiro/specs/` の設計書にも反映する。

## 開発コマンド

以下を標準コマンドとする(ソリューション構成は `structure.md` を参照)。いずれもリポジトリのルートで実行する。

```bash
dotnet build
dotnet test
dotnet format        # コードスタイル整形
```

## コーディング規約

- 各プロジェクト(`.csproj`)共通で以下を設定する(`Directory.Build.props` にまとめる)。
  - `<TargetFramework>net5.0</TargetFramework>`
  - `<Nullable>enable</Nullable>`
  - `<LangVersion>9.0</LangVersion>`(上記「Visual Studio 2019 対応」を参照)
  - テストプロジェクト・実行可能プロジェクトには `<RollForward>LatestMajor</RollForward>` を追加する(この開発環境に.NET 5ランタイムが無くても実行できるようにするため)。
- 名前空間は従来のブロック形式(`namespace X { ... }`)を使用する。file-scoped namespace(`namespace X;`)は使用しない。
- `record` / `record class` はC# 9の機能でありVS2019でも使用可。**`record struct` はC# 10の機能のため使用しない**。値の等価性を持つ構造体が必要な場合は `readonly struct` + `IEquatable<T>` を手書きする。
- レイヤー間の依存方向を一方向に保つ(`structure.md` の依存ルールを参照)。
- 帳票ごとの個別分岐(`if (帳票名 == "請求書") { ... }` のようなコード)をライブラリのコードに持ち込まない。帳票ごとの違いはテンプレートとモデルの側に置く。
- 長さ・座標の単位(mm / pt / CSSの px)を混在させない。ライブラリ内部で使う単位は設計書で定義し統一する。

## テスト戦略

- ユニットテスト: テンプレート展開(モデルの埋め込み、HTMLエスケープ、エラー時の例外)、用紙サイズ・余白などの設定の変換など、純粋なロジックを対象にする。
- PDF出力のテスト: PDFバイナリは生成日時・圧縮・ライブラリのバージョンで変わるため、バイナリの完全一致では比較しない。
  ページ数・ページサイズ・含まれる文字列・埋め込みフォントなど、PDFから取り出せる性質で検証する。具体的な方法はPDF出力方式の選定後に設計書で定める。
- テストフレームワークは xUnit を基本とする。パッケージバージョンは net5.0 世代のものに合わせる(`Microsoft.NET.Test.Sdk` 17.1.0 / `xunit` 2.4.1 / `xunit.runner.visualstudio` 2.4.3。新しすぎる組み合わせはnet5.0でテストが発見されない事象がUtsushiで確認されている)。

## 依存ライブラリ追加時のルール

1. ライセンスが「無償・商用利用可」であることを確認する(同梱・ダウンロードされるネイティブバイナリ等を含む)。
2. `net5.0` で実際にビルド・実行できることを確認する。
3. 既知の脆弱性・メンテナンス状況(最終リリース日など)を確認する。
4. 本ファイルの採用技術の表を更新する。
