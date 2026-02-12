using System.Collections.ObjectModel;
using LlmContextCollector.Models;

namespace LlmContextCollector.Services
{
    public class AiLogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Source { get; set; } = string.Empty; // pl. "Groq", "Ollama (Agent)", "Ollama (Chat)"
        public string Model { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string Response { get; set; } = string.Empty;
    }

    public class AiLogService
    {
        public ObservableCollection<AiLogEntry> Logs { get; } = new();

        public void Log(string source, string model, string prompt, string response)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Logs.Insert(0, new AiLogEntry
                {
                    Source = source,
                    Model = model,
                    Prompt = prompt,
                    Response = response
                });

                // Limitáljuk a memóriában tartott logok számát
                if (Logs.Count > 50)
                {
                    Logs.RemoveAt(Logs.Count - 1);
                }
            });
        }

        public void Clear()
        {
            Logs.Clear();
        }
    }
}