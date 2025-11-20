namespace LlmContextCollector.Models
{
    public class FileNode
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public List<FileNode> Children { get; set; } = new();
        public FileNode? Parent { get; set; }
        public bool IsExpanded { get; set; } = false;
        public bool IsSelectedInTree { get; set; } = false;
        
        public bool IsVisible { get; set; } = true;

        public bool IsContentMatch { get; set; } = false;
        public bool IsPathMatch { get; set; } = false;
    }
}