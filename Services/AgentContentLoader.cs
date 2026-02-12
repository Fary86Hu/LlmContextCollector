using System.Text;
using LlmContextCollector.Models;

namespace LlmContextCollector.Services
{
    public class AgentContentLoader
    {
        private readonly AppState _appState;

        public AgentContentLoader(AppState appState)
        {
            _appState = appState;
        }

        public async Task<string> LoadContentAsync(IEnumerable<string> relativePaths)
        {
            if (string.IsNullOrEmpty(_appState.ProjectRoot)) return string.Empty;

            var sb = new StringBuilder();

            foreach (var relPath in relativePaths)
            {
                var fullPath = Path.Combine(_appState.ProjectRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
                
                if (File.Exists(fullPath))
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(fullPath);
                        sb.AppendLine($"--- Fájl: {relPath} ---");
                        sb.AppendLine(content);
                        sb.AppendLine();
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"--- Fájl: {relPath} (HIBA) ---");
                        sb.AppendLine($"Nem sikerült beolvasni: {ex.Message}");
                        sb.AppendLine();
                    }
                }
            }

            return sb.ToString();
        }
    }
}