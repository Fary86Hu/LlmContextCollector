using LlmContextCollector.AI;
using LlmContextCollector.Models;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace LlmContextCollector.Services
{
    public class ChatService
    {
        private readonly AppState _appState;
        private readonly OllamaService _ollamaService;
        private readonly AiProviderFactory _providerFactory;
        private readonly JsonStorageService _storage;

        public ObservableCollection<ChatMessage> Messages { get; } = new();
        public bool IsGenerating { get; private set; }
        public string CurrentResponseSnippet { get; private set; } = string.Empty;

        private CancellationTokenSource? _cts;

        public ChatService(AppState appState, OllamaService ollamaService, AiProviderFactory providerFactory, JsonStorageService storage)
        {
            _appState = appState;
            _ollamaService = ollamaService;
            _providerFactory = providerFactory;
            _storage = storage;
        }

        private string GetStorageKey()
        {
            if (string.IsNullOrEmpty(_appState.ProjectRoot)) return "global_chat.json";
            var hash = Convert.ToBase64String(Encoding.UTF8.GetBytes(_appState.ProjectRoot)).Replace("=", "").Replace("/", "_").Replace("+", "-");
            return $"chat_history_{hash}.json";
        }

        public async Task LoadHistoryAsync()
        {
            Messages.Clear();
            var history = await _storage.ReadFromFileAsync<List<ChatMessage>>(GetStorageKey());
            if (history != null)
            {
                foreach (var msg in history) Messages.Add(msg);
            }
            _appState.NotifyStateChanged(nameof(Messages));
        }

        public async Task SaveHistoryAsync()
        {
            await _storage.WriteToFileAsync(GetStorageKey(), Messages.ToList());
        }

        public async Task SendMessageAsync(string input, string systemPrompt, string filesContext, bool forceRefreshContext = false)
        {
            if (string.IsNullOrWhiteSpace(input) || IsGenerating) return;

            bool shouldAddContext = !Messages.Any() || forceRefreshContext;

            if (shouldAddContext)
            {
                var sb = new StringBuilder();
                if (forceRefreshContext) sb.AppendLine("[KONTEXTUS FRISSÍTVE]");
                else sb.AppendLine("A feladatod kizárólag a feladat előkészítése. Segíts átgondolni a problémát, tisztázni a követelményeket. Tegyél fel kérdéseket.");
                
                sb.AppendLine("\nKONTEXTUS:");
                if (_appState.ChatIncludeSystem && !string.IsNullOrWhiteSpace(systemPrompt)) 
                    sb.AppendLine($"\n--- Rendszerutasítások ---\n{systemPrompt}");
                
                if (_appState.ChatIncludePrompt && !string.IsNullOrWhiteSpace(_appState.PromptText)) 
                    sb.AppendLine($"\n--- Felhasználói Prompt ---\n{_appState.PromptText}");
                
                if (_appState.ChatIncludeFiles && !string.IsNullOrWhiteSpace(filesContext)) 
                    sb.AppendLine($"\n--- Fájlok tartalma ---\n{filesContext}");

                Messages.Add(new ChatMessage { Role = "system", Content = sb.ToString() });
                _appState.IsContextDirty = false;
            }

            Messages.Add(new ChatMessage { Role = "user", Content = input });            IsGenerating = true;
            CurrentResponseSnippet = string.Empty;
            _cts = new CancellationTokenSource();

            try
            {
                if (_appState.ChatModelId == Guid.Empty)
                {
                    var apiMessages = Messages.Select(m => new { role = m.Role, content = m.Content }).ToList<object>();
                    var responseSb = new StringBuilder();

                    await foreach (var token in _ollamaService.GetChatResponseStreamAsync(apiMessages, _cts.Token))
                    {
                        responseSb.Append(token);
                        CurrentResponseSnippet = responseSb.ToString();
                        _appState.NotifyStateChanged(nameof(CurrentResponseSnippet));
                    }
                    Messages.Add(new ChatMessage { Role = "assistant", Content = CurrentResponseSnippet + (_cts.IsCancellationRequested ? " [MEGSZAKÍTVA]" : "") });
                }
                else
                {
                    var provider = _providerFactory.GetProvider(_appState.ChatModelId);
                    var fullPromptSb = new StringBuilder();
                    foreach (var m in Messages)
                    {
                        fullPromptSb.AppendLine($"### {m.Role.ToUpper()}:\n{m.Content}\n");
                    }

                    var response = await provider.GenerateAsync(fullPromptSb.ToString(), null, _cts.Token);
                    Messages.Add(new ChatMessage { Role = "assistant", Content = response });
                }
                await SaveHistoryAsync();
            }
            catch (OperationCanceledException)
            {
                Messages.Add(new ChatMessage { Role = "assistant", Content = CurrentResponseSnippet + " [MEGSZAKÍTVA]" });
            }
            catch (Exception ex)
            {
                Messages.Add(new ChatMessage { Role = "assistant", Content = $"[HIBA]: {ex.Message}" });
            }
            finally
            {
                IsGenerating = false;
                CurrentResponseSnippet = string.Empty;
                _cts?.Dispose();
                _cts = null;
                _appState.NotifyStateChanged(nameof(IsGenerating));
            }
        }

        public void Abort() => _cts?.Cancel();

        public async Task Clear()
        {
            Messages.Clear();
            await SaveHistoryAsync();
            _appState.NotifyStateChanged(nameof(Messages));
        }
    }
}