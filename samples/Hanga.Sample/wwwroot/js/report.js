// 見た目に関わるJavaScriptの例: 金額を3桁区切りにし、請求書の合計を計算する
document.addEventListener('DOMContentLoaded', function () {
  var sum = 0;
  document.querySelectorAll('td.money').forEach(function (td) {
    var v = parseFloat(td.textContent);
    if (td.classList.contains('amount')) sum += v;
    td.textContent = '¥' + Math.round(v).toLocaleString('ja-JP');
  });
  var total = document.getElementById('total');
  if (total) total.textContent = '¥' + Math.round(sum).toLocaleString('ja-JP');
});
