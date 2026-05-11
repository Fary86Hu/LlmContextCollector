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
                                 "Elemezd a megadott projektstruktúrát és a meglévő fájlokat. " +
                                 "Ha további fájlokra van szükséged a feladat megértéséhez vagy megoldásához, listázd ki azokat a pontos relatív útvonalukkal. " +
                                 "Ha úgy gondolod, hogy minden szükséges információ megvan, jelezd a felhasználónak.";

                    // Fixen beküldjük a fájlokat és a struktúrát, függetlenül a UI checkboxoktól
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

                    var requestedFiles = _parserService.ExtractPotentialFilePaths(lastResponse, allProjectPaths);
                    var newlyAdded = await _fileContextService.AddPathsToContextAsync(requestedFiles, referenceDepth: 1);

                    if (newlyAdded == 0)
                    {
                        _appState.StatusText = "Agent: Minden kért fájl a kontextusban van.";
                        break;
                    }

                    _appState.StatusText = $"Agent: {newlyAdded} új fájl hozzáadva (iteráció {iterations})...";
                    currentInput = "A kért fájlokat és azok referenciáit (depth 1) hozzáadtam a kontextushoz. Van még valami, amire szükséged van, vagy folytathatjuk?";
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