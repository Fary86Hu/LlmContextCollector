using LlmContextCollector.Models;
using LlmContextCollector.AI.Search;
using LlmContextCollector.AI.Embeddings;
using System.IO;
using System.Text;
using System.Linq;

namespace LlmContextCollector.Services
{
    public class RelevanceFinderService
    {
        private readonly AppState _appState;
        private readonly EmbeddingIndexService _indexService;
        private readonly IEmbeddingProvider _embeddingProvider;
        private readonly SemanticSearchService _semanticSearchService;
        private readonly OllamaService _ollamaService;
        private readonly CodeStructureExtractor _extractor;

        public RelevanceFinderService(
            AppState appState, 
            EmbeddingIndexService indexService, 
            IEmbeddingProvider embeddingProvider, 
            SemanticSearchService semanticSearchService,
            OllamaService ollamaService,
            CodeStructureExtractor extractor)
        {
            _appState = appState;
            _indexService = indexService;
            _embeddingProvider = embeddingProvider;
            _semanticSearchService = semanticSearchService;
            _ollamaService = ollamaService;
            _extractor = extractor;
        }

        public async Task<List<RelevanceResult>> FindRelevantFilesIterativelyAsync(
            Func<string, string, Task> onMessageUpdate, 
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_appState.ProjectRoot)) return new List<RelevanceResult>();

            var selectedFiles = new HashSet<string>(_appState.SelectedFilesForContext, StringComparer.OrdinalIgnoreCase);
            var allFileNodes = new List<FileNode>();
            
            void Collect(IEnumerable<FileNode> nodes)
            {
                foreach (var n in nodes)
                {
                    if (n.IsDirectory) Collect(n.Children);
                    else if (n.IsVisible) allFileNodes.Add(n);
                }
            }
            Collect(_appState.FileTree);

            var projectStructure = BuildStructureString(allFileNodes);
            var maxRounds = 4;

            for (int round = 1; round <= maxRounds; round++)
            {
                if (ct.IsCancellationRequested) break;

                var contextContent = await BuildCurrentContextAsync(selectedFiles);
                var prompt = BuildAgentPrompt(projectStructure, contextContent, _appState.PromptText);

                // Jelezzük a UI-nak az ágens "kérését" (gondolatát)
                await onMessageUpdate($"ROUND {round} - Ágens Prompt", prompt);

                var responseSb = new StringBuilder();
                
                // Streameljük a választ élőben
                await foreach (var token in _ollamaService.GetAiResponseStream(prompt, ct))
                {
                    responseSb.Append(token);
                    // Küldjük a UI-nak a részleges választ az aktuális körhöz
                    await onMessageUpdate($"ROUND {round} - AI Válasz", responseSb.ToString());
                }

                var finalResponse = responseSb.ToString();

                if (finalResponse.Contains("READY", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                var foundFiles = ParseFileList(finalResponse, allFileNodes);
                var newFiles = foundFiles.Where(f => !selectedFiles.Contains(f)).ToList();

                if (!newFiles.Any())
                {
                    break;
                }

                foreach (var nf in newFiles) selectedFiles.Add(nf);
            }

            return selectedFiles.Select(f => new RelevanceResult { FilePath = f, Score = 1.0 }).ToList();
        }

        private string BuildStructureString(List<FileNode> allFiles)
        {
            var sb = new StringBuilder();
            foreach (var file in allFiles)
            {
                sb.AppendLine(Path.GetRelativePath(_appState.ProjectRoot, file.FullPath).Replace('\\', '/'));
            }
            return sb.ToString();
        }

        private async Task<string> BuildCurrentContextAsync(HashSet<string> files)
        {
            var sb = new StringBuilder();
            foreach (var relPath in files)
            {
                var fullPath = Path.Combine(_appState.ProjectRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fullPath))
                {
                    var content = await File.ReadAllTextAsync(fullPath);
                    var structure = _extractor.ExtractStructure(content, relPath);
                    sb.AppendLine($"--- Fájl: {relPath} ---");
                    sb.AppendLine(structure);
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        private string BuildAgentPrompt(string structure, string currentContext, string userTask)
        {
            return $@"
Ön egy Szoftver Architect Kereső Ágens. A feladata, hogy a projekt struktúrájából kiválassza az összes olyan fájlt, amelyre szükség van a feladat megértéséhez és implementálásához.

PROJEKT STRUKTÚRA:
{structure}

MÁR KIVÁLASZTOTT FÁJLOK TARTALMA (STRUKTÚRA):
{currentContext}

FELHASZNÁLÓI FELADAT:
{userTask}

UTASÍTÁSOK:
1. Elemezze a feladatot és a már meglevő fájlokat. 
2. Keressen további releváns fájlokat (Service-ek, DTO-k, Entity-k, Repository-k, Configuration fájlok), amelyekre szükség lehet.
3. Ha talált ilyen fájlokat, sorolja fel a relatív elérési útjaikat, minden fájlt külön sorba.
4. Ha úgy ítéli meg, hogy minden szükséges információ rendelkezésre áll, írja le a 'READY' szót.
5. NE írjon magyarázatot, csak a fájllistát vagy a READY szót.
";
        }

        private List<string> ParseFileList(string response, List<FileNode> allNodes)
        {
            var lines = response.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new List<string>();

            foreach (var line in lines)
            {
                var cleanLine = line.Trim().Trim('-').Trim('*').Trim();
                var match = allNodes.FirstOrDefault(n => Path.GetRelativePath(_appState.ProjectRoot, n.FullPath).Replace('\\', '/').Equals(cleanLine, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    result.Add(cleanLine);
                }
            }
            return result;
        }

        public async Task<List<RelevanceResult>> FindRelevantFilesWithAgentAsync()
        {
            // Ezt a metódust az iteratív váltja fel, de kompatibilitás miatt megtartható vagy átirányítható
            return await FindRelevantFilesIterativelyAsync(null);
        }

        public async Task<List<RelevanceResult>> FindRelevantFilesAsync(bool searchInCode, bool searchInAdo)
        {
            var embeddingIndex = _indexService.GetIndex();
            var chunkContents = _indexService.GetChunkContents();

            if (_appState.IsSemanticIndexBuilding || embeddingIndex == null || chunkContents == null || !embeddingIndex.Any())
            {
                _appState.StatusText = "A szemantikus index építése folyamatban van, vagy még nem áll készen. Kis türelmet.";
                return new List<RelevanceResult>();
            }
            
            var queryVectors = new List<float[]>();
            var rawQueryText = _appState.PromptText;

            if (!string.IsNullOrWhiteSpace(rawQueryText))
            {
                var promptVector = await _embeddingProvider.EmbedAsync(rawQueryText);
                if (promptVector.Length > 0) queryVectors.Add(promptVector);
            }

            var contextFileChunks = new List<string>();
            foreach (var fileRelPath in _appState.SelectedFilesForContext)
            {
                contextFileChunks.AddRange(_indexService.GetChunksForFile(fileRelPath));
            }

            if (contextFileChunks.Any())
            {
                var centroidVector = await QueryBuilders.CentroidAsync(_embeddingProvider, contextFileChunks);
                if (centroidVector.Length > 0)
                {
                    if (queryVectors.Count > 0) queryVectors.Add(queryVectors[0]); 
                    queryVectors.Add(centroidVector);
                }
                rawQueryText += " " + string.Join(" ", contextFileChunks.Take(3).Select(c => c.Substring(0, System.Math.Min(c.Length, 150))));
            }
            
            if (!queryVectors.Any())
            {
                _appState.StatusText = "Nincs elegendő kontextus a kereséshez.";
                return new List<RelevanceResult>();
            }

            var multiQuery = new MultiQuery(queryVectors.ToArray());
            var config = new SearchConfig(); 
            var contextFileSet = _appState.SelectedFilesForContext.ToHashSet();

            if (!searchInCode && !searchInAdo)
            {
                return new List<RelevanceResult>();
            }

            HashSet<string>? filesToIncludeSet = null;
            if (searchInCode != searchInAdo)
            {
                filesToIncludeSet = new HashSet<string>();
                var allIndexedPaths = embeddingIndex.Keys.Select(SemanticSearchService.GetPathFromChunkKey).Distinct();
                foreach (var path in allIndexedPaths)
                {
                    var isAdo = path.StartsWith("[ADO]");
                    if ((searchInAdo && isAdo) || (searchInCode && !isAdo))
                    {
                        filesToIncludeSet.Add(path);
                    }
                }
            }

            var results = _semanticSearchService.RankRelevantFiles(multiQuery, rawQueryText, embeddingIndex, chunkContents, config, contextFileSet, filesToInclude: filesToIncludeSet);

            results.ForEach(r => r.SimilarTo = null);
            
            return results;
        }

        public async Task<List<RelevanceResult>> ScoreGivenFilesAsync(IEnumerable<string> filesToScore)
        {
            var embeddingIndex = _indexService.GetIndex();
            var chunkContents = _indexService.GetChunkContents();

            if (_appState.IsSemanticIndexBuilding || embeddingIndex == null || chunkContents == null || !embeddingIndex.Any())
            {
                _appState.StatusText = "A szemantikus index építése folyamatban van, vagy még nem áll készen. Kis türelmet.";
                return new List<RelevanceResult>();
            }

            var queryVectors = new List<float[]>();
            var rawQueryText = _appState.PromptText;

            if (!string.IsNullOrWhiteSpace(rawQueryText))
            {
                var promptVector = await _embeddingProvider.EmbedAsync(rawQueryText);
                if (promptVector.Length > 0) queryVectors.Add(promptVector);
            }

            var contextFileChunks = new List<string>();
            foreach (var fileRelPath in _appState.SelectedFilesForContext)
            {
                contextFileChunks.AddRange(_indexService.GetChunksForFile(fileRelPath));
            }

            if (contextFileChunks.Any())
            {
                var centroidVector = await QueryBuilders.CentroidAsync(_embeddingProvider, contextFileChunks);
                if (centroidVector.Length > 0) queryVectors.Add(centroidVector);
                rawQueryText += " " + string.Join(" ", contextFileChunks.Take(5).Select(c => c.Substring(0, System.Math.Min(c.Length, 200))));
            }
            
            if (!queryVectors.Any())
            {
                _appState.StatusText = "Nincs elegendő kontextus a kereséshez.";
                return new List<RelevanceResult>();
            }

            var multiQuery = new MultiQuery(queryVectors.ToArray());
            var config = new SearchConfig();
            var filesToScoreSet = filesToScore.ToHashSet();

            var results = _semanticSearchService.RankRelevantFiles(multiQuery, rawQueryText, embeddingIndex, chunkContents, config, filesToInclude: filesToScoreSet);
            
            results.ForEach(r => r.SimilarTo = null);
            
            return results;
        }
    }
}