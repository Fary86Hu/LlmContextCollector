using LlmContextCollector.AI.Embeddings;
using LlmContextCollector.AI.Search;
using LlmContextCollector.Models;
using System.Collections.Concurrent;
using System.Text;
using LlmContextCollector.AI.Embeddings.Chunking;

namespace LlmContextCollector.Services
{
    public class EmbeddingIndexService
    {
        private readonly IEmbeddingProvider _embeddingProvider;
        private readonly AppState _appState;
        private readonly JsonEmbeddingCache _cache;
        private readonly CodeStructureExtractor _codeExtractor;
        private readonly IChunker _chunker;
        private readonly ConcurrentDictionary<string, float[]> _embeddingIndex = new();
        private readonly ConcurrentDictionary<string, string> _chunkContents = new();
        private Task? _currentIndexingTask;
        private CancellationTokenSource _cancellationTokenSource = new();
        private const string AdoFilePrefix = "[ADO]";

        public EmbeddingIndexService(IEmbeddingProvider embeddingProvider, AppState appState, JsonEmbeddingCache cache, CodeStructureExtractor codeExtractor, IChunker chunker)
        {
            _embeddingProvider = embeddingProvider;
            _appState = appState;
            _cache = cache;
            _codeExtractor = codeExtractor;
            _chunker = chunker;
        }

        public void ClearIndex()
        {
            _embeddingIndex.Clear();
            _chunkContents.Clear();
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
                catch (OperationCanceledException) { }
                catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is OperationCanceledException)) { }
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

            var corpus = allFileNodes
                .Select(node => {
                    try { return (path: Path.GetRelativePath(projectRootForTask, node.FullPath).Replace('\\', '/'), fullPath: node.FullPath); }
                    catch (ArgumentException) { return (path: null, fullPath: null); }
                })
                .Where(x => x.path != null)
                .Select(x => (x.path!, x.fullPath!))
                .ToList();

            _currentIndexingTask = Task.Run(() => BuildIndexInternalAsync(corpus, "Szemantikus keresési index (Kód)", token, useStructure: true), token);
        }

        public void StartBuildingAdoIndex()
        {
            if (!_appState.AdoDocsExist) return;
            CancelIndexing();
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            var adoDocs = Directory.GetFiles(_appState.AdoDocsPath, "*.txt");
            var corpus = adoDocs.Select(fullPath => (path: $"{AdoFilePrefix}{Path.GetFileName(fullPath)}", fullPath: fullPath)).ToList();
            _currentIndexingTask = Task.Run(() => BuildIndexInternalAsync(corpus, "Szemantikus keresési index (ADO)", token, useStructure: false), token);
        }

        private async Task BuildIndexInternalAsync(List<(string path, string fullPath)> corpus, string statusPrefix, CancellationToken ct, bool useStructure)
        {
            try
            {
                _appState.IsSemanticIndexBuilding = true;
                _appState.StatusText = $"{statusPrefix} építése... (Fájlok elemzése)";
                var cacheVersionPrefix = $"{_embeddingProvider.ModelIdentifier}|{_chunker.GetConfigForCacheKey()}";

                var itemsToProcess = new List<(string cacheKey, string chunkKey, string text)>();
                var totalChunks = 0;
                var processedFiles = 0;

                foreach (var (indexPath, fullPath) in corpus)
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        var content = await File.ReadAllTextAsync(fullPath, ct);
                        var textToChunk = useStructure ? _codeExtractor.ExtractStructure(content, indexPath) : content;

                        var chunks = _chunker.Chunk(textToChunk).ToList();
                        totalChunks += chunks.Count;

                        for (int i = 0; i < chunks.Count; i++)
                        {
                            var chunkContent = chunks[i];
                            var chunkKey = SemanticSearchService.CreateChunkKey(indexPath, i);

                            var textToEmbed = $"File Path: {indexPath}\n\nContent:\n{chunkContent}";
                            _chunkContents[chunkKey] = chunkContent;

                            var cacheKey = JsonEmbeddingCache.KeyFor(fullPath, textToEmbed, cacheVersionPrefix);

                            if (_cache.TryGet(cacheKey, out var vec))
                            {
                                _embeddingIndex[chunkKey] = vec;
                            }
                            else
                            {
                                itemsToProcess.Add((cacheKey, chunkKey, textToEmbed));
                            }
                        }
                        processedFiles++;
                        if (processedFiles % 20 == 0) _appState.StatusText = $"{statusPrefix} építése... ({processedFiles}/{corpus.Count} fájl feldolgozva)";
                    }
                    catch { }
                }

                if (!corpus.Any()) { _appState.StatusText = $"{statusPrefix} frissítve (nincs fájl)."; return; }
                _appState.StatusText = $"{statusPrefix} építése... ({itemsToProcess.Count} darab beágyazása)";

                const int batchSize = 16;
                for (int i = 0; i < itemsToProcess.Count; i += batchSize)
                {
                    if (ct.IsCancellationRequested) return;
                    var batch = itemsToProcess.Skip(i).Take(batchSize).ToList();
                    var textsToEmbedInBatch = batch.Select(c => c.text).ToList();
                    try
                    {
                        var embeds = await _embeddingProvider.EmbedBatchAsync(textsToEmbedInBatch, ct);
                        for (int j = 0; j < batch.Count; j++)
                        {
                            if (ct.IsCancellationRequested) return;
                            _embeddingIndex[batch[j].chunkKey] = embeds[j];
                            _cache.Set(batch[j].cacheKey, embeds[j]);
                        }
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Error embedding batch: {ex.Message}"); }
                    _appState.StatusText = $"{statusPrefix} építése... ({_embeddingIndex.Count}/{totalChunks} darab indexelve)";
                }

                if (itemsToProcess.Any()) _cache.Persist();
                if (!ct.IsCancellationRequested) _appState.StatusText = $"{statusPrefix} frissítve ({totalChunks} darab indexelve {corpus.Count} fájlból).";
            }
            finally
            {
                if (ct.IsCancellationRequested) _appState.StatusText = "Index építés megszakítva.";
                _appState.IsSemanticIndexBuilding = false;
            }
        }

        public IReadOnlyDictionary<string, float[]>? GetIndex() => _embeddingIndex.IsEmpty ? null : _embeddingIndex;
        public IReadOnlyDictionary<string, string>? GetChunkContents() => _chunkContents.IsEmpty ? null : _chunkContents;

        public IEnumerable<string> GetChunksForFile(string filePath)
        {
            var contents = GetChunkContents();
            if (contents == null) yield break;

            foreach (var (key, content) in contents)
            {
                if (SemanticSearchService.GetPathFromChunkKey(key).Equals(filePath, StringComparison.OrdinalIgnoreCase))
                {
                    yield return content;
                }
            }
        }
    }
}