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

        public int? DefaultDimension => null; // Ollama modellek dimenziója változó, dinamikusan derül ki

        public string ModelIdentifier => $"ollama-{_appState.OllamaEmbeddingModel}";

        public async Task<float[]> EmbedAsync(string input, CancellationToken ct = default)
        {
            var baseUrl = _appState.OllamaApiUrl.TrimEnd('/');
            if (baseUrl.EndsWith("/v1"))
            {
                baseUrl = baseUrl.Substring(0, baseUrl.LastIndexOf("/v1"));
            }

            var client = _httpClientFactory.CreateClient("OllamaEmbed");

            var request = new
            {
                model = _appState.OllamaEmbeddingModel,
                prompt = input
            };

            var response = await client.PostAsJsonAsync($"{baseUrl}/api/embeddings", request, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"Ollama API hiba ({response.StatusCode}): {errorBody}");
            }

            var result = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(cancellationToken: ct);
            
            if (result?.Embedding == null)
            {
                throw new InvalidOperationException($"Az Ollama nem küldött embeddinget a '{_appState.OllamaEmbeddingModel}' modellhez.");
            }

            return result.Embedding.Select(d => (float)d).ToArray();
        }

        public async Task<float[][]> EmbedBatchAsync(IEnumerable<string> inputs, CancellationToken ct = default)
        {
            var inputList = inputs.ToList();
            var results = new float[inputList.Count][];

            for (int i = 0; i < inputList.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                // Itt nem nyeljük le a hibát, hogy az IndexingService megállhasson és jelezhesse a bajt
                results[i] = await EmbedAsync(inputList[i], ct);
            }

            return results;
        }

        private class OllamaEmbeddingResponse
        {
            [JsonPropertyName("embedding")]
            public double[]? Embedding { get; set; }
        }
    }
}