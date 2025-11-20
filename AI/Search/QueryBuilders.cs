using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LlmContextCollector.AI.Embeddings;

namespace LlmContextCollector.AI.Search
{
    public class QueryBuilders
    {
        public static async Task<float[]> CentroidAsync(IEmbeddingProvider provider, IEnumerable<string> texts, CancellationToken ct = default)
        {
            var arr = texts.ToArray();
            if (arr.Length == 0) return System.Array.Empty<float>();

            var embs = await provider.EmbedBatchAsync(arr, ct);
            if (!embs.Any() || embs[0].Length == 0) return System.Array.Empty<float>();

            return AverageVectors(embs.ToList());
        }

        public static float[] Centroid(IEnumerable<float[]> vectors)
        {
             var list = vectors.ToList();
             if (!list.Any()) return System.Array.Empty<float>();
             return AverageVectors(list);
        }

        private static float[] AverageVectors(List<float[]> vectors)
        {
            if (!vectors.Any()) return System.Array.Empty<float>();
            
            var d = vectors[0].Length;
            if (d == 0) return System.Array.Empty<float>();

            var v = new float[d];
            foreach(var emb in vectors)
            {
                if (emb.Length != d) continue;
                for (int j = 0; j < d; j++) v[j] += emb[j];
            }
            
            var inv = 1f / vectors.Count;
            for (int j = 0; j < d; j++) v[j] *= inv;
            
            double s = 0; for (int j = 0; j < d; j++) s += v[j] * v[j];
            var n = (float)System.Math.Sqrt(s);
            if (n > 0f) for (int j = 0; j < d; j++) v[j] /= n;

            return v;
        }
    }
}