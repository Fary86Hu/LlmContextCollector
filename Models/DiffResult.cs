namespace LlmContextCollector.Models
{
    public class DiffResult
    {
        public string Path { get; set; } = string.Empty;
        public string? OriginalPath { get; set; } 
        public string OldContent { get; set; } = string.Empty;
        public string NewContent { get; set; } = string.Empty;
        public DiffStatus Status { get; set; }
        public bool IsSelectedForAccept { get; set; } = true;
        
        public string Explanation { get; set; } = string.Empty;
        
        public bool PatchFailed { get; set; }
        public string FailedPatchContent { get; set; } = string.Empty;

        public bool IsRename => !string.IsNullOrEmpty(OriginalPath) && OriginalPath != Path;
    }

    public enum DiffStatus
    {
        New,
        Modified,
        Deleted,
        Accepted,
        Error,
        NewFromModified,
        Renamed,
        AlreadyApplied
    }
}