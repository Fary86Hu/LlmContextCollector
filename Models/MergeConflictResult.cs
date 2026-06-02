namespace LlmContextCollector.Models
{
    public class MergeConflictResult
    {
        public string Path { get; set; } = string.Empty;
        public string OldContent { get; set; } = string.Empty;
        public string OursContent { get; set; } = string.Empty;
        public string TheirsContent { get; set; } = string.Empty;
        public string ResolvedContent { get; set; } = string.Empty;
        public bool IsResolved { get; set; }
        public string Explanation { get; set; } = string.Empty;
    }
}