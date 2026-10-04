using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hanga.TestApp.Controllers
{
    /// <summary>ログインが必要なAPI。ビューのJavaScriptが表示時に値を取得する。</summary>
    [ApiController]
    [Authorize]
    [Route("api/orders")]
    public class OrdersApiController : ControllerBase
    {
        [HttpGet("{id}")]
        public object Get(int id) => new
        {
            orderNo = "ORD-" + id,
            customer = "株式会社サンプル(" + User.Identity!.Name + " が取得)",
            total = 1234567,
        };
    }
}
