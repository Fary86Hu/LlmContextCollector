using LlmContextCollector.AI.Embeddings;
using LlmContextCollector.AI.Search;
using LlmContextCollector.Models;
using System.Collections.Concurrent;
using System.Text;

namespace LlmContextCollector.Services
{
    public class EmbeddingIndexService
    {
        private readonly IEmbeddingProvider _embeddingProvider;
        private readonly AppState _appState;
        private readonly JsonEmbeddingCache _cache;
        private readonly CodeStructureExtractor _codeExtractor;
        private readonly ConcurrentDictionary<string, float[]> _embeddingIndex = new();
        private Task? _currentIndexingTask;
        private CancellationTokenSource _cancellationTokenSource = new();
        private const string AdoFilePrefix = "[ADO]";

        public EmbeddingIndexService(IEmbeddingProvider embeddingProvider, AppState appState, JsonEmbeddingCache cache, CodeStructureExtractor codeExtractor)
        {
            _embeddingProvider = embeddingProvider;
            _appState = appState;
            _cache = cache;
            _codeExtractor = codeExtractor;
        }

        public void ClearIndex()
        {
            _embeddingIndex.Clear();
        }

        public void CancelIndexing()
        {
            if (_currentIndexingTask != null && !_currentIndexingTask.IsCompleted)
            {
                try
                {
                    _cancellationTokenSource.Cancel();
                    _currentIndexingTask.Wait(TimeSpan.FromSeconds(1));
                }
                catch (OperationCanceledException) { /* Elvárt viselkedés */ }
                catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is OperationCanceledException)) { /* Elvárt viselkedés */ }
                finally
                {
                    _cancellationTokenSource.Dispose();
                }
            }
        }

        public void StartBuildingIndex(List<FileNode> allFileNodes)
        {
            CancelIndexing();

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            var projectRootForTask = _appState.ProjectRoot;
            
            var corpus = new List<(string path, string content, string key)>();
            foreach (var node in allFileNodes)
            {
                try
                {
                    var relPath = Path.GetRelativePath(projectRootForTask, node.FullPath).Replace('\\', '/');
                    corpus.Add((relPath, node.FullPath, ""));
                }
                catch (ArgumentException)
                {
                    continue;
                }
            }
            
            _currentIndexingTask = Task.Run(() => BuildIndexInternalAsync(corpus, "Szemantikus keresési index (Kód)", token, useStructure: true), token);
        }

        public void StartBuildingAdoIndex()
        {
            if (!_appState.AdoDocsExist) return;
            CancelIndexing();

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            var adoDocs = Directory.GetFiles(_appState.AdoDocsPath, "*.txt");
            var corpus = adoDocs.Select(fullPath => 
                (path: $"{AdoFilePrefix}{Path.GetFileName(fullPath)}", fullPath, key: "")).ToList();

            _currentIndexingTask = Task.Run(() => BuildIndexInternalAsync(corpus, "Szemantikus keresési index (ADO)", token, useStructure: false), token);
        }

        private async Task BuildIndexInternalAsync(List<(string path, string fullPath, string key)> corpus, string statusPrefix, CancellationToken ct, bool useStructure)
        {
            try
            {
                _appState.IsSemanticIndexBuilding = true;
                _appState.StatusText = $"{statusPrefix} építése...";
                
                var itemsToProcess = new List<(string key, string indexPath, string text)>();
                foreach (var (indexPath, fullPath, _) in corpus)
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        var content = await File.ReadAllTextAsync(fullPath, ct);
                        var textToEmbed = useStructure ? _codeExtractor.ExtractStructure(content, indexPath) : content;
                        var key = JsonEmbeddingCache.KeyFor(fullPath, textToEmbed);
                        
                        if (_cache.TryGet(key, out var vec))
                        {
                            _embeddingIndex[indexPath] = vec;
                        }
                        else
                        {
                            itemsToProcess.Add((key, indexPath, textToEmbed));
                        }
                    }
                    catch { /* Ignore files that can't be read */ }
                }
                
                if (!itemsToProcess.Any() && !corpus.Any())
                {
                    _appState.StatusText = $"{statusPrefix} frissítve (nincs fájl).";
                    return;
                }
                
                _appState.StatusText = $"{statusPrefix} építése... ({_embeddingIndex.Count}/{corpus.Count})";

                const int batchSize = 16;
                for (int i = 0; i < itemsToProcess.Count; i += batchSize)
                {
                    if (ct.IsCancellationRequested) return;

                    var batch = itemsToProcess.Skip(i).Take(batchSize).ToList();
                    var textsToEmbed = batch.Select(c => c.text).ToList();

                    try
                    {
                        var embeds = await _embeddingProvider.EmbedBatchAsync(textsToEmbed, ct);
                        for (int j = 0; j < batch.Count; j++)
                        {
                            if (ct.IsCancellationRequested) return;
                            _embeddingIndex[batch[j].indexPath] = embeds[j];
                            _cache.Set(batch[j].key, embeds[j]);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error embedding batch: {ex.Message}");
                    }

                    _appState.StatusText = $"{statusPrefix} építése... ({_embeddingIndex.Count}/{corpus.Count})";
                }

                if (itemsToProcess.Any())
                {
                    _cache.Persist();
                }

                if (!ct.IsCancellationRequested)
                {
                    _appState.StatusText = $"{statusPrefix} frissítve ({corpus.Count} fájl feldolgozva, összesen {_embeddingIndex.Count} indexelve).";
                }
            }
            finally
            {
                if (ct.IsCancellationRequested)
                {
                    _appState.StatusText = "Index építés megszakítva.";
                }
                _appState.IsSemanticIndexBuilding = false;
            }
        }

        public IReadOnlyDictionary<string, float[]>? GetIndex() => _embeddingIndex.IsEmpty ? null : _embeddingIndex;
    }
}