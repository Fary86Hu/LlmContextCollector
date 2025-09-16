using System.Collections.Concurrent;
using System.Text;

namespace LlmContextCollector.AI.Search;

using LlmContextCollector.AI.Embeddings;

public sealed class SemanticSearchService
{
    readonly IEmbeddingProvider _provider;
    readonly JsonEmbeddingCache _cache;

    public SemanticSearchService(IEmbeddingProvider provider, JsonEmbeddingCache cache)
    {
        _provider = provider;
        _cache = cache;
    }

    public static IEnumerable<string> Chunk(string content, int maxChars = 2000)
    {
        if (content.Length <= maxChars) { yield return content; yield break; }
        var lines = content.Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length + line.Length + 1 > maxChars) { yield return sb.ToString(); sb.Clear(); }
            sb.AppendLine(line);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    public static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb) + 1e-12);
    }

    public async Task<(IReadOnlyList<(string path, double score)> results, Dictionary<string, float[]> vectors)> RankRelevantFilesAsync(
        string query,
        IEnumerable<(string path, string content)> corpus,
        CancellationToken ct = default)
    {
        var chunks = new List<(string path, string chunk)>();
        foreach (var (path, content) in corpus)
            foreach (var ch in Chunk(content))
                chunks.Add((path, ch));

        var keys = chunks.Select(x => (x.path, x.chunk, key: JsonEmbeddingCache.KeyFor(x.path, x.chunk))).ToList();

        var missing = new List<(string key, string text)>();
        var vecs = new ConcurrentDictionary<string, float[]>();
        foreach (var k in keys)
            if (_cache.TryGet(k.key, out var v)) vecs[k.key] = v; else missing.Add((k.key, k.chunk));

        const int batch = 32;
        for (int i = 0; i < missing.Count; i += batch)
        {
            var slice = missing.Skip(i).Take(batch).ToList();
            var embeds = await _provider.EmbedBatchAsync(slice.Select(s => s.text), ct);
            for (int j = 0; j < slice.Count; j++)
            {
                vecs[slice[j].key] = embeds[j];
                _cache.Set(slice[j].key, embeds[j]);
            }
        }

        var q = await _provider.EmbedAsync(query, ct);

        var byPath = new Dictionary<string, double>();
        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            var v = vecs[k.key];
            var s = Cosine(v, q);
            if (!byPath.TryAdd(k.path, s)) byPath[k.path] = Math.Max(byPath[k.path], s);
        }

        var ranked = byPath.OrderByDescending(x => x.Value).Select(x => (x.Key, x.Value)).ToList();
        return (ranked, vecs.ToDictionary(k => k.Key, v => v.Value));
    }
}
