using LlmContextCollector.Models;
using System.Text;

namespace LlmContextCollector.Services
{
    public class ContextAgentService
    {
        private readonly AppState _appState;
        private readonly ChatService _chatService;
        private readonly FileContextService _fileContextService;
        private readonly LlmResponseParserService _parserService;
        private readonly ContextProcessingService _contextProcessingService;

        public ContextAgentService(
            AppState appState, 
            ChatService chatService, 
            FileContextService fileContextService, 
            LlmResponseParserService parserService,
            ContextProcessingService contextProcessingService)
        {
            _appState = appState;
            _chatService = chatService;
            _fileContextService = fileContextService;
            _parserService = parserService;
            _contextProcessingService = contextProcessingService;
        }

        public async Task RunAgentAsync(string userGoal)
        {
            if (_appState.IsAgentRunning) return;
            _appState.IsAgentRunning = true;
            _appState.StatusText = "Agent indítása: releváns fájlok keresése...";

            try
            {
                var currentInput = $"A feladat: {userGoal}\n\nKérlek vizsgáld meg a projekt struktúráját, és jelezd, mely fájlok tartalmára van szükséged. Csak az útvonalakat sorold fel listában.";
                int iterations = 0;
                const int MaxIterations = 3;

                while (iterations < MaxIterations)
                {
                    iterations++;
                    var system = "Te egy technikai asszisztens vagy. A feladatod a projekt kontextusának összeállítása a felhasználói kéréshez. " +
                                 "Elemezd a megadott projektstruktúrát és a meglévő fájlokat.\n\n" +
                                 "HA ÚJ FÁJLRA VAN SZÜKSÉGED, HASZNÁLD AZ ALÁBBI SZINTAXIST:\n" +
                                 "1. Csak a fájl hozzáadása (alapértelmezett, nincs szükség referenciákra): Írd le simán az útvonalat. Például: Services/UserService.cs\n" +
                                 "2. Fájl és belső referenciái (függőségei) hozzáadása: Írj [REFS] jelzést a fájl útvonala mellé. Például: Services/UserService.cs [REFS]\n" +
                                 "3. Egy osztályra / fájlra hivatkozó összes többi fájl hozzáadása (visszahivatkozások): Írj [REFERENCING] jelzést az útvonal mellé. Például: Services/UserService.cs [REFERENCING]\n\n" +
                                 "Ha úgy gondolod, hogy minden szükséges információ megvan, jelezd a felhasználónak.";

                    var filesContext = await _contextProcessingService.BuildContextForClipboardAsync(
                        includePrompt: false, 
                        includeSystemPrompt: false, 
                        includeFiles: true, 
                        includeProjectTree: true, 
                        _appState.SelectedFilesForContext);

                    await _chatService.SendMessageAsync(currentInput, system, filesContext, forceRefreshContext: true, clearHistory: iterations == 1);

                    var lastResponse = _chatService.Messages.LastOrDefault(m => m.Role == "assistant")?.Content;
                    if (string.IsNullOrEmpty(lastResponse)) break;

                    var allProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Utils.FileTreeHelper.GetAllFilePaths(_appState.FileTree, allProjectPaths, _appState.ProjectRoot);

                    var requests = _parserService.ExtractFileContextRequests(lastResponse, allProjectPaths);
                    int newlyAdded = 0;

                    foreach (var req in requests)
                    {
                        newlyAdded += await _fileContextService.AddPathsToContextAsync(
                            new[] { req.Path }, 
                            referenceDepth: req.IncludeReferences ? 1 : 0, 
                            includeReferencing: req.IncludeReferencing);
                    }

                    if (newlyAdded == 0)
                    {
                        _appState.StatusText = "Agent: Minden kért fájl a kontextusban van.";
                        _chatService.Messages.Add(new ChatMessage 
                        { 
                            Role = "system", 
                            Content = $"[Agent - Kör {iterations}] Nem találtam új hozzáandó fájlt a kontextushoz. Az ügynök leáll." 
                        });
                        await _chatService.SaveHistoryAsync();
                        break;
                    }

                    var addedPathsInfo = requests.Select(r => $"- {r.Path}" + (r.IncludeReferences ? " [REFS]" : "") + (r.IncludeReferencing ? " [REFERENCING]" : ""));
                    _chatService.Messages.Add(new ChatMessage 
                    { 
                        Role = "system", 
                        Content = $"[Agent - Kör {iterations}] Automatikusan hozzáadva {newlyAdded} fájl/kapcsolat a kontextushoz:\n" + 
                                  string.Join("\n", addedPathsInfo.Take(10)) + 
                                  (requests.Count > 10 ? "\n..." : "")
                    });
                    await _chatService.SaveHistoryAsync();

                    _appState.StatusText = $"Agent: {newlyAdded} új bejegyzés hozzáadva (iteráció {iterations})...";
                    currentInput = "A kért fájlokat a kért függőségi beállításokkal sikeresen hozzáadtam a kontextushoz. Van még valami, amire szükséged van, vagy folytathatjuk?";
                }

                _appState.StatusText = "Agent befejezte a kontextus építését.";
                _appState.RequestWorkbenchFocus(WorkbenchTab.Chat);
            }
            catch (Exception ex)
            {
                _appState.StatusText = $"Agent hiba: {ex.Message}";
            }
            finally
            {
                _appState.IsAgentRunning = false;
            }
        }
    }
}