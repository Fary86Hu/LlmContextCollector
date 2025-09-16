namespace LlmContextCollector.AI.Embeddings;

public interface IEmbeddingProvider
{
    Task<float[]> EmbedAsync(string input, CancellationToken ct = default);
    Task<float[][]> EmbedBatchAsync(IEnumerable<string> inputs, CancellationToken ct = default);
    int? DefaultDimension { get; }
}