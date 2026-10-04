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

        /// <summary>ページの JavaScript がダイアログ(alert・confirm・prompt)を出した。「OK」で閉じて続けた(要件4.6)。</summary>
        Dialog,

        /// <summary>帳票を開いた後に、ページが別の URL へ移動しようとした。移動を止めて、元のページを PDF にした(要件2.7)。</summary>
        BlockedNavigation,

        /// <summary>1 ページ化で、内容の高さが用紙の高さの上限を超えたため、複数ページになった(要件5.5)。</summary>
        SinglePageOverflow,
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
