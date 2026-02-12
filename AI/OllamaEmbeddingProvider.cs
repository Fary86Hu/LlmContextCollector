using LlmContextCollector.AI.Embeddings;
using LlmContextCollector.Services;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmContextCollector.AI
{
    public class OllamaEmbeddingProvider : IEmbeddingProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AppState _appState;

        public OllamaEmbeddingProvider(IHttpClientFactory httpClientFactory, AppState appState)
        {
            _httpClientFactory = httpClientFactory;
            _appState = appState;
        }

        public int? DefaultDimension => null;

        public string ModelIdentifier => $"ollama-{_appState.OllamaEmbeddingModel}";

        public async Task<float[]> EmbedAsync(string input, CancellationToken ct = default)
        {
            var results = await EmbedBatchAsync(new[] { input }, ct);
            return results.Length > 0 ? results[0] : Array.Empty<float>();
        }

        public async Task<float[][]> EmbedBatchAsync(IEnumerable<string> inputs, CancellationToken ct = default)
        {
            var inputList = inputs.ToList();
            if (inputList.Count == 0) return Array.Empty<float[]>();

            var baseUrl = _appState.OllamaApiUrl.TrimEnd('/');
            if (baseUrl.EndsWith("/v1"))
            {
                baseUrl = baseUrl.Substring(0, baseUrl.LastIndexOf("/v1"));
            }

            var client = _httpClientFactory.CreateClient("OllamaEmbed");

            // Az Ollama /api/embed végpontja elfogad egy tömböt az 'input' mezőben
            var request = new
            {
                model = _appState.OllamaEmbeddingModel,
                input = inputList
            };

            var response = await client.PostAsJsonAsync($"{baseUrl}/api/embed", request, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"Ollama API hiba ({response.StatusCode}): {errorBody}");
            }

            var result = await response.Content.ReadFromJsonAsync<OllamaBatchEmbeddingResponse>(cancellationToken: ct);
            
            if (result?.Embeddings == null)
            {
                throw new InvalidOperationException($"Az Ollama nem küldött embeddingeket.");
            }

            return result.Embeddings.Select(emb => emb.Select(d => (float)d).ToArray()).ToArray();
        }

        private class OllamaBatchEmbeddingResponse
        {
            [JsonPropertyName("embeddings")]
            public double[][]? Embeddings { get; set; }
        }
    }
}