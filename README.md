# Hanga(版画)

ASP.NET Core 5 MVC のビュー(CSHTML)から、帳票の PDF を簡易的に作る .NET 5 / C# 向けライブラリ。

画面用に書いたビューを、そのまま PDF にします。レイアウト・`~/` のパス・タグヘルパー・印刷用の CSS・JavaScript による描画、
ログインが必要な API から表示時に取得する値まで、画面と同じように PDF に反映されます。
「版画」という名前は、版(ビュー)を一度作れば、データを載せて同じ体裁の刷り物(PDF)を何枚でも刷れる、という役割に由来します。

## 使い方

```csharp
// Startup.ConfigureServices
services.AddHanga(Configuration.GetSection("Hanga")); // Chromium の場所などを appsettings.json から読む

// コントローラーの PDF 用アクション
public async Task<IActionResult> OrderPdf(int id)
{
    var pdf = new Cshtml2Pdf(this, "Order", _orders.Find(id));
    pdf.Options.Orientation = PageOrientation.Landscape; // 既定は A4 縦
    return await pdf.ToActionResultAsync("注文明細.pdf", PdfDisposition.Inline);
}
```

詳しくは [`docs/ライブラリの使い方.md`](docs/ライブラリの使い方.md) を参照。

### 主な特長

- 画面用のビューをそのまま使える(ASP.NET Core MVC のビュー描画の仕組みでHTMLにする)
- ビューが読み込む CSS・JavaScript・API は、アプリ自身の処理にネットワークを通さずに渡し、オペレーターのログインを引き継ぐ
- 用紙サイズ・向き・余白・倍率・画面の幅を用紙に収める・1 ページにする・ページ番号などをオプションで指定できる
- 人名・住所の外字・異体字に対応(IPAmj明朝 などの外字用フォントを使う)
- 複数のオペレーターが同時に使える(帳票ごとに独立したブラウザの環境を使い、取り違えない)
- 崩れた PDF・値の欠けた PDF を黙って返さない(原因と段階が分かる例外、警告と厳格な扱い)
- Office・商用ライブラリは不要(.NET 5 / ASP.NET Core / PuppeteerSharp 18.1.0 / ヘッドレス Chromium)

### 本番環境に必要なもの

- Chromium(Google Chrome など)。PDF を作るサーバーにインストールし、場所を設定する
- 外字を扱う場合は IPAmj明朝 などの外字用フォント
- Chromium を更新する前の確認: [`docs/Chromium更新前の検証手順.md`](docs/Chromium更新前の検証手順.md)

## 開発

このリポジトリは AWS Kiro スタイルの spec 駆動開発に従う。開発時の方針・制約・サブエージェントの使い方は [`CLAUDE.md`](CLAUDE.md) にまとめている。

```bash
dotnet build
dotnet test      # Chromium を使うテストがある。環境変数 HANGA_TEST_CHROMIUM で場所を指定する
dotnet format
```

- `src/` — `Hanga.Core`(例外・設定) / `Hanga.Templating`(ビューの HTML 化) / `Hanga.Rendering`(Chromium による PDF 化) / `Hanga`(公開 API と ASP.NET Core との接続)
- `tests/` — ユニットテストと、テスト用アプリ(`Hanga.TestApp`)を使った結合テスト
- `samples/Hanga.Sample` — サンプルアプリ(使い方の例と、検証ツールの対象)
- `tools/Hanga.ChromiumCheck` — Chromium 更新前の検証ツール、`tools/probe-fonts` — 字形の確認に使うフォントの生成
- `docs/` — 利用の手引き・検証手順・PDF 出力方式の検証レポート・開発環境メモ
- `.kiro/` — 方針(steering)と仕様(specs)

呼び出し元プロダクトの開発環境が Visual Studio 2019(.NET 5、C# 9.0 まで)であるため、その制約に合わせている。
詳細は [`.kiro/steering/tech.md`](.kiro/steering/tech.md) を参照。

## ライセンス

[MIT License](LICENSE)。使用している OSS ライブラリのライセンスは [`.kiro/steering/tech.md`](.kiro/steering/tech.md) を参照。
