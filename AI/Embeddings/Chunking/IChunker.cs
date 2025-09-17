namespace LlmContextCollector.AI.Embeddings.Chunking
{
    public interface IChunker
    {
        IEnumerable<string> Chunk(string text);
        string GetConfigForCacheKey();
    }
}