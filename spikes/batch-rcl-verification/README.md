# 別の実行ファイル(バッチ)から帳票ライブラリを使う検証(スパイク)

`docs/PDF出力方式検証レポート.md`「6.5」の検証に使ったコード。製品コードではなく、`Hanga.sln` には含めない。

- `Reports/` — 帳票ライブラリ(Razor クラスライブラリ)。帳票ビュー `Views/Invoice/Invoice.cshtml`、帳票用のレイアウト、`wwwroot/css/report.css`。
- `Batch/` — バッチ(`Microsoft.NET.Sdk` のコンソールアプリ)。Webサーバーを起動せずに帳票ビューを HTML にし、
  `/_content/Reports/css/report.css` をパイプラインにプロセス内で要求する。最後に `RESULT: OK` / `RESULT: FAILED <件数>` を出す。
- `BatchWeb/` — 同じプログラム(`Batch/Program.cs`)を `Microsoft.NET.Sdk.Web` で作ったもの。

## 実行

```bash
# ビルドの出力から(引数 explicit でビューのアセンブリを明示的に加える)
dotnet build BatchWeb && dotnet BatchWeb/bin/Debug/net5.0/BatchWeb.dll [explicit]

# 発行して実行
dotnet publish BatchWeb -c Release -o <出力先> && dotnet <出力先>/BatchWeb.dll

# .NET 5 のランタイムで動かす(docs/開発環境メモ.md「本物の .NET 5 ランタイムで動かす方法」。Linux では libssl1.1 が必要)
dotnet build BatchWeb -r linux-x64 --self-contained true -p:RollForward=Disable
LD_LIBRARY_PATH=<libssl1.1 の展開先> BatchWeb/bin/Debug/net5.0/linux-x64/BatchWeb
```

`RollForward=LatestMajor` のまま `dotnet ...dll` で動かすと .NET 10 のランタイムで動き、.NET 5 形式の静的Webアセットの一覧(`*.StaticWebAssets.xml`)が読まれない。
