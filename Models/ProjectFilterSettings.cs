using System.Collections.Generic;

namespace LlmContextCollector.Models
{
    public class ProjectFilterSettings
    {
        public string IgnorePatterns { get; set; } = string.Empty;
        public Dictionary<string, bool> ExtensionFilters { get; set; } = new();
        public List<AttachableDocument> AttachableDocuments { get; set; } = new();
        public string BuildCommand { get; set; } = string.Empty;
        public string RunCommand { get; set; } = string.Empty;
        public string SelectedLaunchProfile { get; set; } = string.Empty;
        public string LocalizationResourcePath { get; set; } = string.Empty;
    }
}