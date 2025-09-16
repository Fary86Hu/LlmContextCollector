using LlmContextCollector.Models;
using LlmContextCollector.AI;
using LlmContextCollector.AI.Embeddings;

namespace LlmContextCollector.Services
{
    public class RelevanceFinderService
    {
        private readonly AppState _appState;
        private readonly EmbeddingIndexService _indexService;
        private readonly IEmbeddingProvider _embeddingProvider;
        private readonly SemanticSearchService _semanticSearchService;

        public RelevanceFinderService(AppState appState, EmbeddingIndexService indexService, IEmbeddingProvider embeddingProvider, SemanticSearchService semanticSearchService)
        {
            _appState = appState;
            _indexService = indexService;
            _embeddingProvider = embeddingProvider;
            _semanticSearchService = semanticSearchService;
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
            var contextVectors = new List<float[]>();

            if (!string.IsNullOrWhiteSpace(_appState.PromptText))
            {
                var promptVector = await _embeddingProvider.EmbedAsync(_appState.PromptText);
                contextVectors.Add(promptVector);
            }

            foreach (var fileRelPath in _appState.SelectedFilesForContext)
            {
                var fileVectors = _indexService.GetVectorsForFile(fileRelPath).ToList();
                if (fileVectors.Any())
                {
                    // Average the vectors of all chunks for a file to get a single representative vector
                    var avgVector = AverageVectors(fileVectors);
                    contextVectors.Add(avgVector);
                }
            }

            if (!contextVectors.Any())
            {
                _appState.StatusText = "Nincs elegendő kontextus a kereséshez.";
                return new List<RelevanceResult>();
            }

            // 2. Súlyozott átlag query vektor létrehozása
            var queryVector = AverageVectors(contextVectors); // Simple average for now, could be weighted later

            // 3. Jelölt fájlok szűrése és pontozása a SemanticSearchService segítségével
            var contextFileSet = _appState.SelectedFilesForContext.ToHashSet();
            
            var results = _semanticSearchService.RankBySimilarity(queryVector, embeddingIndex, 30, filesToExclude: contextFileSet);

            // A 'SimilarTo' mezőt már nem tudjuk egyszerűen kitölteni, mert aggregált lekérdezést használunk.
            // A relevanciát a teljes kontextushoz képest mérjük.
            results.ForEach(r => r.SimilarTo = null);
            
            return results;
        }

        private float[] AverageVectors(List<float[]> vectors)
        {
            if (!vectors.Any()) return Array.Empty<float>();
            
            var dimension = vectors.First().Length;
            var avgVector = new float[dimension];

            foreach (var vector in vectors)
            {
                for (int i = 0; i < dimension; i++)
                {
                    avgVector[i] += vector[i];
                }
            }

            var count = (float)vectors.Count;
            for (int i = 0; i < dimension; i++)
            {
                avgVector[i] /= count;
            }

            return avgVector;
        }
    }
}