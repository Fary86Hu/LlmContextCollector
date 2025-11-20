namespace LlmContextCollector.Models
{
    public class DiffResult
    {
        public string Path { get; set; } = string.Empty;
        public string OldContent { get; set; } = string.Empty;
        public string NewContent { get; set; } = string.Empty;
        public DiffStatus Status { get; set; }
        public bool IsSelectedForAccept { get; set; } = true;
        
        // Új mező a fájl-specifikus magyarázat tárolására
        public string Explanation { get; set; } = string.Empty;
    }

    public enum DiffStatus
    {
        New,
        Modified,
        Deleted,
        Accepted,
        Error,
        NewFromModified
    }
}