# Chromium 更新前の検証手順(運用部門向け)

Hanga は、サーバーにインストールされた Chromium(Google Chrome など)を使って帳票の PDF を作ります。
脆弱性対応などで Chromium をバージョンアップすると、PDF が作れなくなったり、見た目が変わったりする可能性があります。
**本番サーバーの Chromium を更新する前に、検証環境でこの手順を実行し、帳票が変わらず出力されることを確かめてください。**

検証には、検証ツール `Hanga.ChromiumCheck` を使います。ツールの中でサンプルの帳票アプリを起動し(このサーバーの中だけで動き、外部には公開しません)、
サンプルの帳票(請求書・幅の広い表・外字)の PDF を作って、更新前に作った「基準」と比べます。

## 準備(初回のみ)

1. 開発担当から、検証ツールを受け取ります(`dotnet publish tools/Hanga.ChromiumCheck -c Release -o <フォルダ>` で作ったフォルダ)。
2. 検証環境のサーバーに、本番と同じ方法で Chromium と外字用フォント(IPAmj明朝 など)をインストールしておきます。

## 手順

### 1. 更新前: 基準を作る

更新前の Chromium で、基準を作ります。

```
dotnet Hanga.ChromiumCheck.dll baseline --chromium "C:\Program Files\Google\Chrome\Application\chrome.exe" --gaiji-font "IPAmj明朝" --out C:\HangaCheck\baseline
```

- `--chromium`: Chromium の実行ファイル(本番の設定 `Hanga:ChromiumExecutablePath` と同じもの)
- `--gaiji-font`: 外字用フォントの名前(本番の設定 `Hanga:GaijiFontFamily` と同じもの。使っていなければ省略)
- `--arg`: Chromium に渡す起動引数(本番の設定 `Hanga:ChromiumArguments` と同じもの。複数あれば `--arg` を繰り返す)
- `--out`: 基準を保存するフォルダ

「基準を作りました。」と表示されれば成功です。`--out` のフォルダに、帳票の PDF と Chromium の版(`chromium-version.txt`)が保存されます。

### 2. Chromium を更新する

検証環境のサーバーで、Chromium をバージョンアップします。

### 3. 更新後: 基準と比べる

```
dotnet Hanga.ChromiumCheck.dll compare --chromium "C:\Program Files\Google\Chrome\Application\chrome.exe" --gaiji-font "IPAmj明朝" --baseline C:\HangaCheck\baseline --out C:\HangaCheck\after-update
```

結果は画面に表示され、`--out` のフォルダの `report.txt` にも保存されます。

| 終了コード | 意味 | 対応 |
|---|---|---|
| 0 | 違いはありませんでした | 本番の Chromium を更新してかまいません |
| 1 | 違いがありました | 下の「違いがあった場合」を参照 |
| 2 | 検証を実行できませんでした(PDF を作れなかった等) | **本番の Chromium を更新しないでください。** 表示されたエラーを添えて開発担当に連絡してください |

比べる内容: ページ数、ページサイズ、PDF の文字列、各文字の位置とフォント。

### 違いがあった場合

1. `report.txt` に書かれた違いと、`--out` のフォルダの PDF を開いて、基準の PDF と見比べます。
2. 帳票として問題が無い違い(わずかな文字の位置のずれなど)であれば、今回の結果を新しい基準にして(`--out` のフォルダを次回の `--baseline` に使う)、本番の Chromium を更新してかまいません。
3. 問題のある違い(文字が欠ける、ページがずれる、外字が別の字になる等)であれば、**本番の Chromium を更新せず**、`report.txt` と PDF を添えて開発担当に連絡してください。

## 補足

- 基準は、Chromium を更新するたびに作り直してかまいません(更新前に `baseline` を実行する)。
- 検証ツールは Hanga の版ごとに作り直します。Hanga を更新したときは、検証ツールも開発担当から受け取り直してください。
- Hanga 側でも、Chromium を操作するライブラリ(PuppeteerSharp)を差し替えるときに、同じ手順で確かめます。
