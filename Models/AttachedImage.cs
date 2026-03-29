namespace LlmContextCollector.Models
{
    public class AttachedImage
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Base64Thumbnail { get; set; } = string.Empty;
    }
}