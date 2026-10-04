// 見た目に関わるJavaScriptの例: 金額を3桁区切りにし、合計を計算して描画する
document.addEventListener('DOMContentLoaded', function () {
  var sum = 0;
  document.querySelectorAll('td.money').forEach(function (td, i) {
    var v = parseFloat(td.textContent);
    if (i % 2 === 1) sum += v;
    td.textContent = '¥' + Math.round(v).toLocaleString('ja-JP');
  });
  var total = document.getElementById('total');
  total.textContent = '¥' + Math.round(sum).toLocaleString('ja-JP');
  total.className = 'js-made';
  document.fonts.ready.then(function () { window.hangaReady = true; });
});
