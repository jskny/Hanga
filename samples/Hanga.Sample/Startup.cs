using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hanga.Sample
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
                .AddCookie(options => options.LoginPath = "/Account/SignIn");
            services.AddAuthorization();

            // appsettings.json の "Hanga" 節(Chromium の場所・外字用フォント・同時処理数など)から登録する
            services.AddHanga(Configuration.GetSection(HangaOptions.DefaultSectionName));
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
