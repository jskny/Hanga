# PDF出力方式の検証用コード(スパイク)

`docs/PDF出力方式検証レポート.md` の検証に使ったコード。製品コードではなく、`Hanga.sln` には含めない。
Windows Server の検証環境で同じ確認をするときにも使う。

- `console/` — CSHTML(RazorLight)→ HTML → PDF(PuppeteerSharp)を1回実行するコンソールアプリ。
  外部CSS・JavaScript・Webフォント・画像をリソース要求への介入で `wwwroot` から返し、それ以外の通信は遮断する。
- `web/` — ASP.NET Core 5 アプリの中で同じ処理を動かす確認用。`GET /pdf` でPDFを返す。
- `mvc/` — ASP.NET Core 5 アプリ自身のビュー描画機能(`IRazorViewEngine`)で `Views/Home/Index.cshtml`(レイアウト・`~/`・タグヘルパーを含む)をHTMLにし、
  PuppeteerSharp 18.1.0 でPDFにする。`GET /pdf` でPDFを返す。実行例: `dotnet run -- <chromeの実行ファイル>`
- `inproc/` — Cookie認証のAPIから表示時に値を取得するビューを、PDF用エンドポイント(`/order/pdf`)からPDFにする。
  仮想オリジンへの要求をアプリのパイプラインにプロセス内で渡し、オペレーターのCookieを引き継ぐ。
  実行例: `dotnet run -- <chromeの実行ファイル>` の後、`curl -c jar.txt http://127.0.0.1:5079/login` → `curl -b jar.txt -o order.pdf http://127.0.0.1:5079/order/pdf`
  `concurrent-test.sh` は、8人のオペレーターの同時要求で値が取り違えられないかを確かめる。
- `razorlight-mvc-view/` — 上記と同じ `Views` を RazorLight で展開してみる確認用。実行例: `dotnet run -- ../mvc/Views`

## 準備

`console/wwwroot/fonts/report.ttf` に日本語のTrueTypeフォントを1つ置く(リポジトリには含めない)。
例: Linux なら `cp /usr/share/fonts/opentype/ipafont-gothic/ipag.ttf console/wwwroot/fonts/report.ttf`、
Windows なら `C:\Windows\Fonts` から再配布条件を確認したうえでコピーする。

## 実行

```bash
cd console

# PuppeteerSharp の版を指定してビルドし、Chromium の実行ファイルを渡して実行する
dotnet build -c Release -p:PuppeteerVersion=18.1.0 -o out/18.1.0
dotnet out/18.1.0/Poc.dll <chromeの実行ファイル> out.pdf setcontent   # HTMLを直接流し込む方式(推奨)
dotnet out/18.1.0/Poc.dll <chromeの実行ファイル> out.pdf navigate     # Page.navigate でページを開く方式

# Visual Studio 2019 と同じ C# コンパイラ(Roslyn 3.11)でビルドする
dotnet build -c Release --no-incremental -p:Vs2019Compiler=true -p:PuppeteerVersion=18.1.0 -o vs/18.1.0

# .NET 5 ランタイムを同梱して発行する(.NET 5 のランタイムが無い環境で、本物の .NET 5 で動かすため)
dotnet publish -c Release -r linux-x64 --self-contained true -p:PuppeteerVersion=18.1.0 -o sc/18.1.0
```

- 外字用フォントの注入を試すときは、環境変数 `HANGA_GAIJI_FONT=<外字用フォントのファイル>` を付けて setcontent 方式で実行する。
  さらに `HANGA_GAIJI_SRC=local` を付けると、ファイルを転送せず、インストール済みの IPAmj明朝 を名前で参照する。
- この開発環境(Linux)の Chromium は `/opt/pw-browsers/chromium-1194/chrome-linux/chrome`(Chrome 141)。
- コードは Linux のコンテナで root として動かすため `--no-sandbox` を付けている。Windows Server では外して試し、
  起動できない場合はその旨を記録すること(`docs/PDF出力方式検証レポート.md`「未検証の事項」)。
- Ubuntu 24.04 で .NET 5 ランタイムを同梱して動かすと `No usable version of libssl was found` で止まる
  (.NET 5 は OpenSSL 3 に対応していない)。検証では Ubuntu 20.04 の `libssl1.1` を展開して `LD_LIBRARY_PATH` で渡した。
  Windows では OpenSSL を使わないため関係しない。
