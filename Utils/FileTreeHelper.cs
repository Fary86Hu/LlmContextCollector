namespace LlmContextCollector.Utils
{
    using LlmContextCollector.Models;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    public static class FileTreeHelper
    {
        public static void GetAllFileNodes(IEnumerable<FileNode> nodes, List<FileNode> flatList)
        {
            foreach (var node in nodes)
            {
                if (node.IsDirectory) GetAllFileNodes(node.Children, flatList);
                else flatList.Add(node);
            }
        }

        public static void FindSelectedNodes(IEnumerable<FileNode> nodes, List<FileNode> selected)
        {
            foreach (var node in nodes)
            {
                if (node.IsSelectedInTree) selected.Add(node);
                if (node.Children.Any()) FindSelectedNodes(node.Children, selected);
            }
        }

        public static void DeselectAllNodes(IEnumerable<FileNode> nodes)
        {
            foreach (var node in nodes)
            {
                node.IsSelectedInTree = false;
                if (node.Children.Any()) DeselectAllNodes(node.Children);
            }
        }
        
        public static void GetAllFilePaths(IEnumerable<FileNode> nodes, HashSet<string> paths, string root)
        {
            foreach (var node in nodes)
            {
                if (node.IsDirectory) GetAllFilePaths(node.Children, paths, root);
                else paths.Add(Path.GetRelativePath(root, node.FullPath).Replace('\\', '/'));
            }
        }
    }
}