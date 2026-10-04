using System;
using System.Threading.Tasks;
using RazorLight;

namespace Rl
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            var engine = new RazorLightEngineBuilder().UseFileSystemProject(args[0]).UseMemoryCachingProvider().Build();
            foreach (var key in new[] { "Home/Index.cshtml", "Home/IndexWithLayout.cshtml" })
            {
                Console.WriteLine("===== " + key);
                try { Console.WriteLine(await engine.CompileRenderAsync<object?>(key, null)); }
                catch (Exception ex) { Console.WriteLine(ex.GetType().Name + ": " + ex.Message); }
            }
        }
    }
}
