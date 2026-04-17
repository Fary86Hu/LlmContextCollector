using System.Collections.ObjectModel;
using LlmContextCollector.Models;

namespace LlmContextCollector.Services
{
    public enum LogType { Ai, Info, Warning, Error }

    public class LogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public LogType Type { get; set; } = LogType.Info;
        public string Source { get; set; } = string.Empty; 
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        
        // AI specifikus mezők
        public string? Prompt { get; set; }
        public string? Response { get; set; }
        public string? Model { get; set; }
    }

    public class AppLogService
    {
        private readonly AppState _appState;

        public AppLogService(AppState appState)
        {
            _appState = appState;
        }

        public ObservableCollection<LogEntry> Logs { get; } = new();

        public void LogAi(string source, string model, string prompt, string response)
        {
            AddEntry(new LogEntry
            {
                Type = LogType.Ai,
                Source = source,
                Model = model,
                Title = $"AI hívás: {model}",
                Prompt = prompt,
                Response = response
            });
        }

        public void LogInfo(string source, string title, string content = "") 
        {
            if (!_appState.LogInformationLevel) return;
            AddEntry(new LogEntry { Type = LogType.Info, Source = source, Title = title, Content = content });
        }

        public void LogWarning(string source, string title, string content = "") 
            => AddEntry(new LogEntry { Type = LogType.Warning, Source = source, Title = title, Content = content });

        public void LogError(string source, string title, string content = "") 
            => AddEntry(new LogEntry { Type = LogType.Error, Source = source, Title = title, Content = content });

        private void AddEntry(LogEntry entry)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Logs.Insert(0, entry);
                if (Logs.Count > 100)
                {
                    Logs.RemoveAt(Logs.Count - 1);
                }
            });
        }

        public void Clear() => Logs.Clear();
    }
}