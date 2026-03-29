using System;

namespace LlmContextCollector.Models
{
    public class AttachableDocument
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public bool IsSelected { get; set; } = false;

        public AttachableDocument Clone() => (AttachableDocument)this.MemberwiseClone();
    }
}