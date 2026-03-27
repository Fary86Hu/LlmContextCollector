using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LlmContextCollector.Models;

namespace LlmContextCollector.Services
{
    public class AcceptedResponseHistoryService
    {
        private readonly AppState _appState;

        public AcceptedResponseHistoryService(AppState appState)
        {
            _appState = appState;
        }

        private string? GetHistoryFilePath(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot)) return null;
            var projectFolderName = new DirectoryInfo(projectRoot).Name;
            var settingsDir = Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory, projectFolderName);
            Directory.CreateDirectory(settingsDir);
            return Path.Combine(settingsDir, "llm_accepted_history.json");
        }

        public async Task AddEntryAsync(string projectRoot, string explanation, List<DiffResult> acceptedFiles)
        {
            var path = GetHistoryFilePath(projectRoot);
            if (path == null) return;

            List<LlmHistoryEntry> history = await GetHistoryAsync(projectRoot);

            var entry = new LlmHistoryEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTime.Now,
                Explanation = explanation ?? string.Empty,
                Files = acceptedFiles
            };

            history.Insert(0, entry);
            if (history.Count > 20)
            {
                history = history.Take(20).ToList();
            }

            var json = JsonSerializer.Serialize(history);
            await File.WriteAllTextAsync(path, json);
        }

        public async Task<List<LlmHistoryEntry>> GetHistoryAsync(string projectRoot)
        {
            var path = GetHistoryFilePath(projectRoot);
            if (path != null && File.Exists(path))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(path);
                    return JsonSerializer.Deserialize<List<LlmHistoryEntry>>(json) ?? new List<LlmHistoryEntry>();
                }
                catch { }
            }
            return new List<LlmHistoryEntry>();
        }
    }
}