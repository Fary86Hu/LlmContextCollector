namespace LlmContextCollector.Models
{
    public class FileContextRequest
    {
        public string Path { get; set; } = string.Empty;
        public bool IncludeReferences { get; set; }
        public bool IncludeReferencing { get; set; }
    }
}