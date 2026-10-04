using System;

namespace Hanga
{
    /// <summary>
    /// Hanga が出す例外の基底(要件8.1)。失敗した段階(<see cref="Stage"/>)を持つ(要件8.2)。
    /// メッセージに Cookie などの認証情報を含めない(要件8.5)。
    /// </summary>
    public class HangaException : Exception
    {
        public HangaException(string message, HangaStage stage, Exception? innerException = null)
            : base(message, innerException)
        {
            Stage = stage;
        }

        /// <summary>失敗した処理の段階。</summary>
        public HangaStage Stage { get; }
    }
}
