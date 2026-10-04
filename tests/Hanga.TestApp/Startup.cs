using System.Linq;
using Hanga.TestSupport;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hanga.TestApp
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllersWithViews();
            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options => options.LoginPath = "/Account/Login");
            services.AddAuthorization();

            // 設定ファイル(テストでは UseSetting)の "Hanga" 節を読み、Chromium の指定が無ければテスト用の Chromium を使う
            services.AddHanga(Configuration.GetSection(HangaOptions.DefaultSectionName), options =>
            {
                if (string.IsNullOrEmpty(options.ChromiumExecutablePath))
                {
                    options.ChromiumExecutablePath = TestChromium.ExecutablePath;
                    options.ChromiumArguments = TestChromium.Arguments.ToList();
                }
            });
        }

        public void Configure(IApplicationBuilder app)
        {
            app.UseStaticFiles();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapDefaultControllerRoute());
        }
    }
}
