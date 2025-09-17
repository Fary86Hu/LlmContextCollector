using LlmContextCollector.AI.Embeddings.Chunking;
using System.Collections.Generic;

namespace LlmContextCollector.AI.Embeddings.Chunking
{
    public class NullChunker : IChunker
    {
        public IEnumerable<string> Chunk(string text)
        {
            yield break;
        }

        public string GetConfigForCacheKey()
        {
            return "null-chunker";
        }
    }
}