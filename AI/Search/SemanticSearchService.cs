using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LlmContextCollector.AI.Embeddings;
using LlmContextCollector.Models;

namespace LlmContextCollector.AI.Search
{
    public class SearchConfig
    {
        public int Candidates { get; set; } = 200;
        public int Rerank { get; set; } = 60;
        public int TopKPerFile { get; set; } = 3;
        public float WVec { get; set; } = 0.7f;
        public float WName { get; set; } = 0.1f;
        public float WKeyword { get; set; } = 0.15f;
        public float WRecency { get; set; } = 0.05f;
        public float MmrLambda { get; set; } = 0.3f;
        public double MinScoreThreshold { get; set; } = 0.2;
    }

    public class MultiQuery
    {
        public float[][] Q { get; }
        public MultiQuery(params float[][] qs) { Q = qs.Where(x => x?.Length > 0).ToArray(); }
        public double Score(float[] v)
        {
            if (Q.Length == 0) return 0;
            double s = -1;
            foreach (var q in Q) s = System.Math.Max(s, SemanticSearchService.Cosine(q, v));
            return s;
        }
    }

    public static class KeywordUtil
    {
        static readonly Regex Splitter = new Regex(@"[_\-/\\\.\s]+|(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);
        public static HashSet<string> Tokens(string s)
        {
            var t = Splitter.Split(s ?? "").Where(x => x.Length > 1).Select(x => x.ToLowerInvariant());
            return new HashSet<string>(t);
        }
        public static double Coverage(HashSet<string> q, string text)
        {
            if (q.Count == 0 || string.IsNullOrWhiteSpace(text)) return 0;
            var doc = Tokens(text);
            if (doc.Count == 0) return 0;
            var hit = q.Count(x => doc.Contains(x));
            return (double)hit / q.Count;
        }
    }

    public sealed class SemanticSearchService
    {
        private const string ChunkKeySeparator = "::CHUNK::";

        public static double Cosine(float[] a, float[] b)
        {
            double dot = 0, na = 0, nb = 0;
            for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
            var denominator = System.Math.Sqrt(na) * System.Math.Sqrt(nb);
            if (denominator < 1e-12) return 0;
            return dot / denominator;
        }

        public static string CreateChunkKey(string filePath, int chunkIndex) => $"{filePath}{ChunkKeySeparator}{chunkIndex}";
        public static string GetPathFromChunkKey(string chunkKey)
        {
            var separatorIndex = chunkKey.IndexOf(ChunkKeySeparator);
            return separatorIndex != -1 ? chunkKey.Substring(0, separatorIndex) : chunkKey;
        }

        public List<RelevanceResult> RankRelevantFiles(
            MultiQuery multiQuery,
            string rawQuery,
            IReadOnlyDictionary<string, float[]> index,
            IReadOnlyDictionary<string, string> chunkContents,
            SearchConfig cfg,
            HashSet<string>? filesToExclude = null,
            HashSet<string>? filesToInclude = null)
        {
            var queryNameTokens = KeywordUtil.Tokens(rawQuery);
            
            // 1. Initial Candidate Selection (Hybrid Score)
            var candidates = index
                .AsParallel()
                .Select(kvp =>
                {
                    var key = kvp.Key;
                    var chunkVector = kvp.Value;
                    var filePath = GetPathFromChunkKey(key);
                    
                    if (filesToExclude != null && filesToExclude.Contains(filePath))
                        return (key, score: -1.0);
                    
                    if (filesToInclude != null && !filesToInclude.Contains(filePath))
                        return (key, score: -1.0);

                    var vecScore = multiQuery.Score(chunkVector);
                    var nameScore = KeywordUtil.Coverage(queryNameTokens, filePath);
                    var keywordScore = chunkContents.TryGetValue(key, out var content) ? KeywordUtil.Coverage(queryNameTokens, content) : 0;
                    
                    var hybridScore = cfg.WVec * vecScore + cfg.WName * nameScore + cfg.WKeyword * keywordScore;

                    return (key, score: hybridScore);
                })
                .Where(x => x.score > cfg.MinScoreThreshold)
                .OrderByDescending(x => x.score)
                .Take(cfg.Candidates)
                .ToList();

            if (!candidates.Any()) return new List<RelevanceResult>();

            // 2. Rerank with MMR for diversity
            var rerankedKeys = new List<string>();
            var candidatePool = new List<(string key, double score)>(candidates);

            while (rerankedKeys.Count < cfg.Rerank && candidatePool.Any())
            {
                (string key, double score) bestCandidate;
                if (rerankedKeys.Count == 0)
                {
                    bestCandidate = candidatePool[0];
                    rerankedKeys.Add(bestCandidate.key);
                    candidatePool.RemoveAt(0);
                    continue;
                }
                
                double bestMmrScore = double.MinValue;
                int bestCandidateIndex = -1;
                bestCandidate = ("", -1.0);

                for (int i = 0; i < candidatePool.Count; i++)
                {
                    var cand = candidatePool[i];
                    var candVector = index[cand.key];
                    var maxSimToSelected = rerankedKeys.Max(selKey => Cosine(candVector, index[selKey]));
                    
                    var mmrScore = cfg.MmrLambda * cand.score - (1 - cfg.MmrLambda) * maxSimToSelected;

                    if (mmrScore > bestMmrScore)
                    {
                        bestMmrScore = mmrScore;
                        bestCandidate = cand;
                        bestCandidateIndex = i;
                    }
                }

                if (bestCandidateIndex != -1)
                {
                    rerankedKeys.Add(bestCandidate.key);
                    candidatePool.RemoveAt(bestCandidateIndex);
                }
                else
                {
                    break; // No more suitable candidates
                }
            }

            // 3. Aggregate scores at the file level
            var chunksByFile = rerankedKeys
                .Select(key => new {
                    FilePath = GetPathFromChunkKey(key),
                    ChunkKey = key,
                    Score = candidates.First(c => c.key == key).score
                })
                .GroupBy(x => x.FilePath);

            // 4. Final file scoring by aggregating top-k chunks
            var finalScores = chunksByFile
                .Select(kvp => {
                    var topChunks = kvp.OrderByDescending(c => c.Score).Take(cfg.TopKPerFile).ToList();
                    var topKSum = topChunks.Sum(c => c.Score);
                    var topChunkContents = topChunks.Select(c => chunkContents.TryGetValue(c.ChunkKey, out var content) ? content : "").ToList();
                    
                    return new RelevanceResult
                    {
                        FilePath = kvp.Key,
                        Score = topKSum,
                        TopChunks = topChunkContents
                    };
                })
                .OrderByDescending(r => r.Score)
                .ToList();

            return finalScores;
        }
    }
}