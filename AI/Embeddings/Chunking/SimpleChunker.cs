using System;
using System.Collections.Generic;

namespace LlmContextCollector.AI.Embeddings.Chunking
{
    public class SimpleChunker : IChunker
    {
        private readonly int _chunkSize;
        private readonly int _overlap;

        public SimpleChunker(int chunkSize = 1000, int overlap = 200)
        {
            _chunkSize = chunkSize;
            _overlap = overlap;
        }

        public IEnumerable<string> Chunk(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                yield break;
            }

            // Ha a szöveg rövidebb, mint egy chunk, egyben visszaadjuk
            if (text.Length <= _chunkSize)
            {
                yield return text;
                yield break;
            }

            int start = 0;
            while (start < text.Length)
            {
                int length = Math.Min(_chunkSize, text.Length - start);
                yield return text.Substring(start, length);

                start += (_chunkSize - _overlap);
                
                // Biztonsági fék, ha az overlap rosszul lenne beállítva
                if (length < _chunkSize) break;
            }
        }

        public string GetConfigForCacheKey() => $"simple-{_chunkSize}-{_overlap}";
    }
}