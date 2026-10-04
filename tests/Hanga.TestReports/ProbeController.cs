using System.Threading;
using Microsoft.AspNetCore.Mvc;

namespace Hanga.TestReports
{
    /// <summary>
    /// 帳票ライブラリにあるコントローラー。バッチでは URL の生成にだけ使い、アクションは実行させないことを確かめる
    /// (batch-pdf-generation の design.md「ホストの組み立て」。バッチには認証・認可が無いため)。
    /// </summary>
    public sealed class ProbeController : Controller
    {
        private static int runCount;

        /// <summary>アクションが実行された回数。</summary>
        public static int RunCount => Volatile.Read(ref runCount);

        public IActionResult Run()
        {
            Interlocked.Increment(ref runCount);
            return Content("実行された");
        }
    }
}
