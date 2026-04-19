using System;

namespace LlmContextCollector.Models
{
    public class ChatSession
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = "Új beszélgetés";
        public DateTime LastModified { get; set; } = DateTime.Now;
    }
}