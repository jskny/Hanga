#!/bin/bash
# 8人のオペレーターが同時にPDFを要求し、各PDFに本人の権限で取得した値が入っているかを確かめる。
# 使い方: dotnet publish -c Release -o out した後、concurrent-test.sh <chromeの実行ファイル>
# (.NET 5 ランタイムで動かす場合の発行方法は ../README.md を参照)
set -u
cd "$(dirname "$0")"
./out/InProc "$1" > concurrent.log 2>&1 &
server=$!
for i in $(seq 1 30); do curl -s -o /dev/null http://127.0.0.1:5079/ && break; sleep 1; done
rm -rf concurrent && mkdir concurrent
for u in $(seq 1 8); do curl -s -c concurrent/jar$u.txt "http://127.0.0.1:5079/login?user=user$u" > /dev/null; done
clients=()
for u in $(seq 1 8); do
  curl -s -b concurrent/jar$u.txt -o concurrent/user$u.pdf -w "user$u http=%{http_code} time=%{time_total}s\n" http://127.0.0.1:5079/order/pdf &
  clients+=($!)
done
wait "${clients[@]}"   # 引数なしの wait はサーバーの終了まで待ってしまうため、curl だけを待つ
kill $server; wait $server 2>/dev/null
for u in $(seq 1 8); do
  got=$(pdftotext concurrent/user$u.pdf - | grep -o "user[0-9]* が取得")
  [ "$got" = "user$u が取得" ] && echo "user$u: OK" || echo "user$u: NG ($got)"
done
