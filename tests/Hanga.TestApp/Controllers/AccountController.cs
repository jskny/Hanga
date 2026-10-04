using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Hanga.TestApp.Controllers
{
    /// <summary>テスト用のログイン。<c>/Account/SignIn?user=名前</c> で、その名前のユーザーとしてログインする。</summary>
    public class AccountController : Controller
    {
        public async Task<IActionResult> SignIn(string user)
        {
            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, user) }, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(new ClaimsPrincipal(identity));
            return Content("signed in: " + user);
        }

        public IActionResult Login() => Content("login page");
    }
}
