using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hanga.Sample.Controllers
{
    /// <summary>請求書の画面が、表示時に JavaScript で値を取得するAPI(ログインが必要)。</summary>
    [ApiController]
    [Authorize]
    [Route("api/customers")]
    public class CustomerApiController : ControllerBase
    {
        [HttpGet("{id}")]
        public object Get(int id) => new
        {
            id,
            name = "株式会社サンプル",
            address = "東京都千代田区丸の内1-1-1",
            person = "髙﨑 太郎",
        };
    }
}
