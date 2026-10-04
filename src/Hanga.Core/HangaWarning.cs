namespace Hanga
{
    /// <summary>警告の種類(要件8.6)。既定では警告として記録し、厳格な扱いではエラーにする(要件8.7)。</summary>
    public enum HangaWarningKind
    {
        /// <summary>許可していない外部への要求を遮断した(要件3.6)。</summary>
        BlockedExternalRequest,

        /// <summary>ページの JavaScript で捕まえられていない例外が発生した(要件4.5)。</summary>
        ScriptError,

        /// <summary>どのフォントにも字形の無い文字がある(要件6.7)。</summary>
        MissingGlyph,
    }

    /// <summary>PDF の生成中に見つかった問題(要件8.6)。Cookie などの認証情報を含めない(要件8.5)。</summary>
    public sealed class HangaWarning
    {
        public HangaWarning(HangaWarningKind kind, string message, string? detail = null)
        {
            Kind = kind;
            Message = message;
            Detail = detail;
        }

        public HangaWarningKind Kind { get; }

        public string Message { get; }

        /// <summary>補足(遮断した URL、字形の無い文字の符号位置など)。</summary>
        public string? Detail { get; }

        public override string ToString() => Detail == null ? $"[{Kind}] {Message}" : $"[{Kind}] {Message} ({Detail})";
    }
}
