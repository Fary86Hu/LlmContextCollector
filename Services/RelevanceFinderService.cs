using LlmContextCollector.Models;
using LlmContextCollector.AI.Search;
using LlmContextCollector.AI.Embeddings;
using System.IO;

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
            var chunkContents = _indexService.GetChunkContents();

            if (_appState.IsSemanticIndexBuilding || embeddingIndex == null || chunkContents == null || !embeddingIndex.Any())
            {
                _appState.StatusText = "A szemantikus index építése folyamatban van, vagy még nem áll készen. Kis türelmet.";
                return new List<RelevanceResult>();
            }
            
            var queryVectors = new List<float[]>();
            var rawQueryText = _appState.PromptText;

            // 1. Prompt vector
            if (!string.IsNullOrWhiteSpace(rawQueryText))
            {
                var promptVector = await _embeddingProvider.EmbedAsync(rawQueryText);
                if (promptVector.Length > 0) queryVectors.Add(promptVector);
            }

            // 2. Context files centroid vector
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
            var config = new SearchConfig(); // Use default config for now
            var contextFileSet = _appState.SelectedFilesForContext.ToHashSet();

            var results = _semanticSearchService.RankRelevantFiles(multiQuery, rawQueryText, embeddingIndex, chunkContents, config, contextFileSet);

            // A 'SimilarTo' mezőt már nem tudjuk egyszerűen kitölteni, mert aggregált lekérdezést használunk.
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

            // 1. Prompt vector
            if (!string.IsNullOrWhiteSpace(rawQueryText))
            {
                var promptVector = await _embeddingProvider.EmbedAsync(rawQueryText);
                if (promptVector.Length > 0) queryVectors.Add(promptVector);
            }

            // 2. Context files centroid vector - use files to score, not just context
            var contextFileChunks = new List<string>();
            foreach (var fileRelPath in _appState.SelectedFilesForContext) // A lekérdezéshez továbbra is a teljes kontextust használjuk
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