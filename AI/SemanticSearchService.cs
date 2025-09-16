using LlmContextCollector.Models;
using System.Collections.Concurrent;
using System.Text;

namespace LlmContextCollector.AI;

using LlmContextCollector.AI.Embeddings;

public sealed class SemanticSearchService
{
    private const string ChunkKeySeparator = "::CHUNK::";

    public static IEnumerable<string> Chunk(string content, int maxChars = 2000, int overlapChars = 200)
    {
        if (string.IsNullOrWhiteSpace(content)) yield break;
        if (content.Length <= maxChars)
        {
            yield return content;
            yield break;
        }

        var sb = new StringBuilder();
        var lines = content.Split('\n');

        foreach (var line in lines)
        {
            if (sb.Length + line.Length + 1 > maxChars)
            {
                yield return sb.ToString();
                // Overlap logic: keep the last few lines for the next chunk
                var currentChunk = sb.ToString();
                var overlapStartIndex = Math.Max(0, currentChunk.Length - overlapChars);
                var overlapText = currentChunk.Substring(overlapStartIndex);
                // Find the first full line in the overlap text
                var firstNewLine = overlapText.IndexOf('\n');
                if (firstNewLine != -1)
                {
                    overlapText = overlapText.Substring(firstNewLine + 1);
                }

                sb.Clear();
                sb.Append(overlapText);
            }
            sb.AppendLine(line);
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    public static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        var denominator = Math.Sqrt(na) * Math.Sqrt(nb);
        if (denominator < 1e-12) return 0; // Avoid division by zero
        return dot / denominator;
    }

    public static string CreateChunkKey(string filePath, int chunkIndex) => $"{filePath}{ChunkKeySeparator}{chunkIndex}";
    public static string GetPathFromChunkKey(string chunkKey)
    {
        var separatorIndex = chunkKey.IndexOf(ChunkKeySeparator);
        return separatorIndex != -1 ? chunkKey.Substring(0, separatorIndex) : chunkKey;
    }

    public List<RelevanceResult> RankBySimilarity(
        float[] queryVector,
        IReadOnlyDictionary<string, float[]> index,
        int topK,
        double minScoreThreshold = 0.2,
        HashSet<string>? filesToExclude = null)
    {
        var scoresByPath = new Dictionary<string, double>();

        foreach (var (chunkKey, chunkVector) in index)
        {
            var filePath = GetPathFromChunkKey(chunkKey);
            if (filesToExclude != null && filesToExclude.Contains(filePath))
            {
                continue;
            }

            var score = Cosine(queryVector, chunkVector);

            if (score > minScoreThreshold)
            {
                if (!scoresByPath.TryAdd(filePath, score))
                {
                    scoresByPath[filePath] = Math.Max(scoresByPath[filePath], score);
                }
            }
        }

        return scoresByPath
            .Select(kvp => new RelevanceResult { FilePath = kvp.Key, Score = kvp.Value })
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();
    }
}