using System.Collections.Generic;

namespace LlmContextCollector.Models
{
    public class ProjectFilterSettings
    {
        public string IgnorePatterns { get; set; } = string.Empty;
        public Dictionary<string, bool> ExtensionFilters { get; set; } = new();
        public List<AttachableDocument> AttachableDocuments { get; set; } = new();
    }
}