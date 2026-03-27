using System;
using System.Collections.Generic;

namespace LlmContextCollector.Models
{
    public class LlmHistoryEntry
    {
        public Guid Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string Explanation { get; set; } = string.Empty;
        public List<DiffResult> Files { get; set; } = new();
    }
}