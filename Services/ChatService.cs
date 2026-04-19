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
        public ObservableCollection<ChatSession> Sessions { get; } = new();
        public Guid? CurrentSessionId { get; private set; }
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

        private string GetProjectHash()
        {
            if (string.IsNullOrEmpty(_appState.ProjectRoot)) return "global";
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(_appState.ProjectRoot)).Replace("=", "").Replace("/", "_").Replace("+", "-");
        }

        private string GetSessionsKey() => $"chat_sessions_{GetProjectHash()}.json";
        private string GetMessagesKey(Guid sessionId) => $"chat_msgs_{sessionId}.json";

        public async Task LoadSessionsAsync()
        {
            Sessions.Clear();
            var list = await _storage.ReadFromFileAsync<List<ChatSession>>(GetSessionsKey());
            if (list != null)
            {
                foreach (var s in list.OrderByDescending(x => x.LastModified)) Sessions.Add(s);
            }
            
            if (!CurrentSessionId.HasValue && Sessions.Any())
            {
                await SwitchToSessionAsync(Sessions.First().Id);
            }
            else if (!Sessions.Any())
            {
                await CreateNewSessionAsync();
            }
        }

        public async Task CreateNewSessionAsync()
        {
            var newSession = new ChatSession();
            Sessions.Insert(0, newSession);
            await SaveSessionsListAsync();
            await SwitchToSessionAsync(newSession.Id);
        }

        public async Task SwitchToSessionAsync(Guid sessionId)
        {
            CurrentSessionId = sessionId;
            Messages.Clear();
            var msgs = await _storage.ReadFromFileAsync<List<ChatMessage>>(GetMessagesKey(sessionId));
            if (msgs != null)
            {
                foreach (var m in msgs) Messages.Add(m);
            }
            _appState.NotifyStateChanged(nameof(Messages));
            _appState.NotifyStateChanged(nameof(CurrentSessionId));
        }

        public async Task DeleteSessionAsync(Guid sessionId)
        {
            var session = Sessions.FirstOrDefault(s => s.Id == sessionId);
            if (session != null)
            {
                Sessions.Remove(session);
                await SaveSessionsListAsync();
                if (CurrentSessionId == sessionId)
                {
                    CurrentSessionId = null;
                    if (Sessions.Any()) await SwitchToSessionAsync(Sessions.First().Id);
                    else await CreateNewSessionAsync();
                }
            }
        }

        private async Task SaveSessionsListAsync()
        {
            await _storage.WriteToFileAsync(GetSessionsKey(), Sessions.ToList());
            _appState.NotifyStateChanged(nameof(Sessions));
        }

        public async Task SaveHistoryAsync()
        {
            if (!CurrentSessionId.HasValue) return;
            await _storage.WriteToFileAsync(GetMessagesKey(CurrentSessionId.Value), Messages.ToList());
            
            var session = Sessions.FirstOrDefault(s => s.Id == CurrentSessionId.Value);
            if (session != null)
            {
                session.LastModified = DateTime.Now;
                await SaveSessionsListAsync();
            }
        }

        public async Task SendMessageAsync(string input, string systemPrompt, string filesContext, bool forceRefreshContext = false, bool clearHistory = false)
        {
            if (string.IsNullOrWhiteSpace(input) || IsGenerating) return;

            if (clearHistory || !CurrentSessionId.HasValue)
            {
                await CreateNewSessionAsync();
            }

            var session = Sessions.FirstOrDefault(s => s.Id == CurrentSessionId);
            if (session != null && session.Title == "Új beszélgetés")
            {
                session.Title = input.Length > 30 ? input.Substring(0, 30) + "..." : input;
                await SaveSessionsListAsync();
            }

            bool shouldAddContext = !Messages.Any() || forceRefreshContext;

            if (shouldAddContext)
            {
                var sb = new StringBuilder();
                if (forceRefreshContext && Messages.Any()) sb.AppendLine("[KONTEXTUS FRISSÍTVE]");
                else sb.AppendLine("A feladatod kizárólag a feladat előkészítése. Segíts átgondolni a problémát, tisztázni a követelményeket. Tegyél fel kérdéseket.");
                
                sb.AppendLine("\nKONTEXTUS:");
                if (!string.IsNullOrWhiteSpace(systemPrompt)) 
                    sb.AppendLine($"\n--- Rendszerutasítások ---\n{systemPrompt}");
                
                if (!string.IsNullOrWhiteSpace(filesContext)) 
                    sb.AppendLine($"\n--- Fájlok tartalma ---\n{filesContext}");

                Messages.Add(new ChatMessage { Role = "system", Content = sb.ToString() });
                _appState.IsContextDirty = false;
            }

            Messages.Add(new ChatMessage { Role = "user", Content = input });
            _appState.RequestWorkbenchFocus(WorkbenchTab.Chat);
            IsGenerating = true;
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