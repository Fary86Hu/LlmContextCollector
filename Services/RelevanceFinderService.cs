using LlmContextCollector.Models;
using LlmContextCollector.AI.Search;
using LlmContextCollector.AI.Embeddings;

namespace LlmContextCollector.Services
{
    public class RelevanceFinderService
    {
        private readonly AppState _appState;
        private readonly EmbeddingIndexService _indexService;
        private readonly IEmbeddingProvider _embeddingProvider;

        public RelevanceFinderService(AppState appState, EmbeddingIndexService indexService, IEmbeddingProvider embeddingProvider)
        {
            _appState = appState;
            _indexService = indexService;
            _embeddingProvider = embeddingProvider;
        }

        public async Task<List<RelevanceResult>> FindRelevantFilesAsync()
        {
            var embeddingIndex = _indexService.GetIndex();
            if (_appState.IsSemanticIndexBuilding || embeddingIndex == null || !embeddingIndex.Any())
            {
                _appState.StatusText = "A szemantikus index építése folyamatban van, vagy még nem áll készen. Kis türelmet.";
                return new List<RelevanceResult>();
            }

            // 1. Kontextus vektorok összegyűjtése (prompt + már kiválasztott fájlok)
            var contextVectors = new Dictionary<string, float[]>();

            if (!string.IsNullOrWhiteSpace(_appState.PromptText))
            {
                var promptVector = await _embeddingProvider.EmbedAsync(_appState.PromptText);
                contextVectors["Prompt"] = promptVector;
            }

            foreach (var fileRelPath in _appState.SelectedFilesForContext)
            {
                if (embeddingIndex.TryGetValue(fileRelPath, out var vector))
                {
                    contextVectors[fileRelPath] = vector;
                }
            }

            if (!contextVectors.Any())
            {
                _appState.StatusText = "Nincs elegendő kontextus a kereséshez.";
                return new List<RelevanceResult>();
            }

            // 2. Jelölt fájlok szűrése és pontozása
            var contextFileSet = _appState.SelectedFilesForContext.ToHashSet();
            var candidateFiles = embeddingIndex.Where(kvp => !contextFileSet.Contains(kvp.Key));

            var results = new List<RelevanceResult>();

            foreach (var (candidatePath, candidateVector) in candidateFiles)
            {
                double maxScore = 0;
                string bestMatch = string.Empty;

                foreach (var (contextKey, contextVector) in contextVectors)
                {
                    var score = SemanticSearchService.Cosine(candidateVector, contextVector);
                    if (score > maxScore)
                    {
                        maxScore = score;
                        bestMatch = contextKey;
                    }
                }

                if (maxScore > 0) // Vagy egy relevánsabb küszöbérték, pl. 0.3
                {
                    results.Add(new RelevanceResult
                    {
                        FilePath = candidatePath,
                        Score = maxScore,
                        SimilarTo = bestMatch
                    });
                }
            }

            // 3. Eredmények rendezése és szűkítése
            return results
                .OrderByDescending(r => r.Score)
                .Take(30)
                .ToList();
        }

        private void GetAllFileNodes(IEnumerable<FileNode> nodes, List<FileNode> flat)
        {
            foreach (var n in nodes)
            {
                if (n.IsDirectory) GetAllFileNodes(n.Children, flat);
                else flat.Add(n);
            }
        }
    }
}