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
        private readonly AgentContentLoader _contentLoader;
        private readonly AgentPromptBuilder _promptBuilder;

        public RelevanceFinderService(
            AppState appState, 
            EmbeddingIndexService indexService, 
            IEmbeddingProvider embeddingProvider, 
            SemanticSearchService semanticSearchService,
            OllamaService ollamaService,
            AgentContentLoader contentLoader,
            AgentPromptBuilder promptBuilder)
        {
            _appState = appState;
            _indexService = indexService;
            _embeddingProvider = embeddingProvider;
            _semanticSearchService = semanticSearchService;
            _ollamaService = ollamaService;
            _contentLoader = contentLoader;
            _promptBuilder = promptBuilder;
        }

        public async Task<List<RelevanceResult>> FindRelevantFilesIterativelyAsync(
            Func<string, string, Task> onMessageUpdate, 
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_appState.ProjectRoot)) return new List<RelevanceResult>();

            var allFileNodes = new List<FileNode>();
            CollectVisibleNodes(_appState.FileTree, allFileNodes);
            
            var session = new AgentSearchSession
            {
                CurrentRound = 1,
                ProjectStructure = BuildStructureString(allFileNodes),
                UserTask = _appState.PromptText,
                FilesSeenAsNames = new HashSet<string>(),
                FilesToLoadFullContent = new HashSet<string>()
            };

            var maxRounds = 5;

            while (session.CurrentRound <= maxRounds)
            {
                if (ct.IsCancellationRequested) break;

                string contentString = string.Empty;
                if (session.FilesToLoadFullContent.Any())
                {
                    await onMessageUpdate($"SYSTEM", $"Fájlok betöltése ({session.FilesToLoadFullContent.Count} db)...");
                    contentString = await _contentLoader.LoadContentAsync(session.FilesToLoadFullContent);
                    
                    foreach (var f in session.FilesToLoadFullContent)
                    {
                        session.FilesSeenAsNames.Add(f);
                    }
                    session.FilesToLoadFullContent.Clear();
                }

                var prompt = _promptBuilder.BuildPrompt(session, contentString);
                
                await onMessageUpdate($"ROUND {session.CurrentRound} - Ágens Prompt", prompt);

                var responseSb = new StringBuilder();
                await foreach (var token in _ollamaService.GetAiResponseStream(prompt, ct))
                {
                    responseSb.Append(token);
                    await onMessageUpdate($"ROUND {session.CurrentRound} - AI Válasz", responseSb.ToString());
                }

                var finalResponse = responseSb.ToString();

                if (finalResponse.Contains("READY", StringComparison.OrdinalIgnoreCase))
                {
                    await onMessageUpdate($"SYSTEM", "Az ágens jelezte, hogy készen áll (READY).");
                    break;
                }

                var requestedFiles = ParseFileList(finalResponse, allFileNodes);
                
                var newFiles = requestedFiles
                    .Where(f => !session.FilesSeenAsNames.Contains(f) && !session.FilesToLoadFullContent.Contains(f))
                    .ToList();

                if (!newFiles.Any())
                {
                    await onMessageUpdate($"SYSTEM", "Az ágens nem kért újabb, eddig ismeretlen fájlokat. Leállás.");
                    break;
                }

                foreach (var nf in newFiles)
                {
                    session.FilesToLoadFullContent.Add(nf);
                }

                session.CurrentRound++;
            }

            return session.FilesSeenAsNames
                .Select(f => new RelevanceResult { FilePath = f, Score = 1.0 })
                .ToList();
        }

        private void CollectVisibleNodes(IEnumerable<FileNode> nodes, List<FileNode> collection)
        {
            foreach (var n in nodes)
            {
                if (n.IsDirectory) CollectVisibleNodes(n.Children, collection);
                else if (n.IsVisible) collection.Add(n);
            }
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

        private List<string> ParseFileList(string response, List<FileNode> allNodes)
        {
            var lines = response.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new List<string>();

            foreach (var line in lines)
            {
                var cleanLine = line.Trim()
                    .TrimStart('-', '*', '>')
                    .TrimEnd(',')
                    .Trim('`', '\'', '"')
                    .Trim();

                if (string.IsNullOrWhiteSpace(cleanLine)) continue;

                var match = allNodes.FirstOrDefault(n => 
                    Path.GetRelativePath(_appState.ProjectRoot, n.FullPath)
                        .Replace('\\', '/')
                        .Equals(cleanLine, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    result.Add(cleanLine);
                }
            }
            return result.Distinct().ToList();
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
            return results;
        }

        public async Task<List<RelevanceResult>> ScoreGivenFilesAsync(IEnumerable<string> filesToScore)
        {
            var embeddingIndex = _indexService.GetIndex();
            var chunkContents = _indexService.GetChunkContents();

            if (_appState.IsSemanticIndexBuilding || embeddingIndex == null || chunkContents == null || !embeddingIndex.Any())
            {
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
            }
            
            if (!queryVectors.Any())
            {
                return new List<RelevanceResult>();
            }

            var multiQuery = new MultiQuery(queryVectors.ToArray());
            var config = new SearchConfig();
            var filesToScoreSet = filesToScore.ToHashSet();

            var results = _semanticSearchService.RankRelevantFiles(multiQuery, rawQueryText, embeddingIndex, chunkContents, config, filesToInclude: filesToScoreSet);
            return results;
        }
    }
}