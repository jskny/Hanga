using Microsoft.AspNetCore.Mvc;

namespace Hanga.TestReports
{
    /// <summary>属性で経路を指定したコントローラー。バッチで URL を生成できることを確かめる(アクションは実行させない)。</summary>
    [Route("routed/probe")]
    public sealed class RoutedProbeController : Controller
    {
        [HttpGet("run")]
        public IActionResult Run() => Content("実行された");
    }
}
