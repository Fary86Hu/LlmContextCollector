using LlmContextCollector.AI.Embeddings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LlmContextCollector.AI
{
    public class NullEmbeddingProvider : IEmbeddingProvider
    {
        public string ModelIdentifier => "none";

        public int? DefaultDimension => null;

        public Task<float[]> EmbedAsync(string input, CancellationToken ct = default)
        {
            return Task.FromResult(Array.Empty<float>());
        }

        public Task<float[][]> EmbedBatchAsync(IEnumerable<string> inputs, CancellationToken ct = default)
        {
            var count = inputs.Count();
            var result = new float[count][];
            for (int i = 0; i < count; i++)
            {
                result[i] = Array.Empty<float>();
            }
            return Task.FromResult(result);
        }
    }
}