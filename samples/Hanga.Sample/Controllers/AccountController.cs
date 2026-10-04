using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Hanga.Sample.Controllers
{
    /// <summary>サンプル用のログイン。<c>/Account/SignIn?user=名前</c> で、その名前のユーザーとしてログインする(実際のアプリでは使わないこと)。</summary>
    public class AccountController : Controller
    {
        public async Task<IActionResult> SignIn(string? user)
        {
            if (string.IsNullOrEmpty(user))
            {
                return Content("/Account/SignIn?user=名前 でログインしてください。");
            }

            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, user) }, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(new ClaimsPrincipal(identity));
            return Content("signed in: " + user);
        }
    }
}
