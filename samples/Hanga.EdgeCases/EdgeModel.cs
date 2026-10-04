using System.Collections.Generic;

namespace Hanga.EdgeCases
{
    /// <summary>エッジケースの帳票のモデル。</summary>
    public sealed class EdgeModel
    {
        public string Text { get; set; } = string.Empty;

        public int Rows { get; set; }

        public List<string> Items { get; set; } = new List<string>();
    }
}
