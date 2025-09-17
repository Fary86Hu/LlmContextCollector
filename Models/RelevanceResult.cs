namespace LlmContextCollector.Models
{
    public class RelevanceResult
    {
        public string FilePath { get; set; } = string.Empty;
        public double Score { get; set; }
        public string? SimilarTo { get; set; }
        public List<string> TopChunks { get; set; } = new();
    }
}