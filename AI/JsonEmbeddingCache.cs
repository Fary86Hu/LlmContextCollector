using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LlmContextCollector.AI.Embeddings;

public sealed class JsonEmbeddingCache
{
    readonly string _path;
    readonly ConcurrentDictionary<string, float[]> _cache = new();

    public string CacheDirectory { get; }

    public JsonEmbeddingCache(string path)
    {
        _path = path;
        CacheDirectory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(CacheDirectory);

        if (File.Exists(_path))
        {
            var json = File.ReadAllText(_path);
            var data = JsonSerializer.Deserialize<Dictionary<string, float[]>>(json);
            if (data != null)
                foreach (var kv in data) _cache.TryAdd(kv.Key, kv.Value);
        }
    }

    public static string KeyFor(string filePath, string content)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(filePath + "|" + content));
        return Convert.ToHexString(bytes);
    }

    public bool TryGet(string key, out float[] vec) => _cache.TryGetValue(key, out vec!);

    public void Set(string key, float[] vec) => _cache[key] = vec;

    public void Persist()
    {
        var json = JsonSerializer.Serialize(_cache);
        File.WriteAllText(_path, json);
    }
}