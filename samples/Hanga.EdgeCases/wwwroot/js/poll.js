// 300 ミリ秒ごとに値を取りに行く(ネットワークが静止しないページ)
setInterval(function () { fetch("/css/edge.css?poll=" + Date.now()); }, 300);
