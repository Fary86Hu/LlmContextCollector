using System;

namespace LlmContextCollector.Models
{
    public class AdoProjectSettings
    {
        public DateTime? LastFullDownloadUtc { get; set; }
        public string LocalizationResourcePath { get; set; } = string.Empty;
    }
}