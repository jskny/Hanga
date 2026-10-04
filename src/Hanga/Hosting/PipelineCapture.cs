using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Hanga.Hosting
{
    /// <summary>捕まえた呼び出し元アプリのパイプライン(design.md「⑤⑥」)。依存性注入のシングルトンに保持する(静的フィールドにしない)。</summary>
    internal sealed class PipelineHolder
    {
        private RequestDelegate? pipeline;

        /// <summary>アプリが組み立てたパイプライン全体。アプリの起動前は null。</summary>
        public RequestDelegate? Pipeline
        {
            get => pipeline;
            set => pipeline = value;
        }

        public RequestDelegate GetRequired() => pipeline
            ?? throw new HangaConfigurationException("アプリのパイプラインがまだ組み立てられていません。AddHanga を呼んだアプリが起動した後に PDF を生成してください。");
    }

    /// <summary>
    /// パイプラインの先頭に、パイプライン全体を捕まえるミドルウェアを加える(要件12.5)。<c>Startup.Configure</c> の変更は不要。
    /// 先頭に加えたミドルウェアの <c>next</c> は、アプリが組み立てたパイプライン全体になる。
    /// </summary>
    internal sealed class PipelineCaptureStartupFilter : IStartupFilter
    {
        private readonly PipelineHolder holder;

        public PipelineCaptureStartupFilter(PipelineHolder holder)
        {
            this.holder = holder;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(rest =>
            {
                holder.Pipeline = rest;
                return rest;
            });
            next(app);
        };
    }
}
