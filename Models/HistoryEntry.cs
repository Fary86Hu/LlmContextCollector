namespace LlmContextCollector.Models
{
    public class HistoryEntry
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public DateTime Timestamp { get; set; }
        public string RootFolder { get; set; } = string.Empty;
        public List<string> SelectedFiles { get; set; } = new();
        public string ExtensionsFilter { get; set; } = string.Empty;
        public string IgnoreFilter { get; set; } = string.Empty;
        public string PromptText { get; set; } = string.Empty;
        public string? SelectedTemplateTitle { get; set; }
    }
}