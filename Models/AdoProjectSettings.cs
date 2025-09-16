using System;

namespace LlmContextCollector.Models
{
    public class AdoProjectSettings
    {
        public string OrganizationUrl { get; set; } = string.Empty;
        public string Project { get; set; } = string.Empty;
        public string Repository { get; set; } = string.Empty;
        public string IterationPath { get; set; } = string.Empty;
        public string Pat { get; set; } = string.Empty;
        public DateTime? LastFullDownloadUtc { get; set; }
    }
}