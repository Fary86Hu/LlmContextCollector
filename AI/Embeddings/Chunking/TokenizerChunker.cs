using Tokenizers.DotNet;

namespace LlmContextCollector.AI.Embeddings.Chunking
{
    public sealed class TokenizerChunker : IChunker
    {
        private readonly Tokenizer _tok;
        private readonly int _maxTokens;
        private readonly int _overlap;

        public TokenizerChunker(Tokenizer tok, int maxTokens = 384, int overlap = 64)
        {
            _tok = tok;
            _maxTokens = maxTokens;
            _overlap = overlap;
        }

        public IEnumerable<string> Chunk(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                yield break;
            }

            var ids = _tok.Encode(text);
            if (ids.Length == 0) yield break;

            var start = 0;
            while (start < ids.Length)
            {
                var end = Math.Min(ids.Length, start + _maxTokens);
                var pieceIds = ids[start..end];
                var piece = _tok.Decode(pieceIds);
                yield return piece;

                if (end == ids.Length) break;

                start += _maxTokens - _overlap;
                if (start >= end)
                {
                    start = end;
                }
            }
        }
        
        public string GetConfigForCacheKey() => $"tok{_maxTokens}-ov{_overlap}";
    }
}