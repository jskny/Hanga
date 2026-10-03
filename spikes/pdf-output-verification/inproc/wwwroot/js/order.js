// 表示時に、相対パスのAPIから非同期で値を取得して描画する(Cookie認証が必要なAPI)
fetch('/api/orders/123', { credentials: 'same-origin' })
  .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
  .then(function (o) {
    document.getElementById('orderNo').textContent = o.orderNo;
    document.getElementById('customer').textContent = o.customer;
    document.getElementById('total').textContent = '¥' + o.total.toLocaleString('ja-JP');
    window.orderLoaded = true;
  })
  .catch(function (e) { document.getElementById('orderNo').textContent = 'エラー: ' + e.message; window.orderLoaded = true; });
