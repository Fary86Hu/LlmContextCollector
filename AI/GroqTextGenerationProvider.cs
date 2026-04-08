using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlmContextCollector.Services;
using System.Net.Http.Headers;

namespace LlmContextCollector.AI
{
    public sealed class GroqTextGenerationProvider : ITextGenerationProvider
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AppState _appState;
        private readonly AppLogService _logService;

        public GroqTextGenerationProvider(IHttpClientFactory httpClientFactory, AppState appState, AppLogService logService)
        {
            _httpClientFactory = httpClientFactory;
            _appState = appState;
            _logService = logService;
        }

        public async Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
        {
            var apiKey = _appState.GroqApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("Groq API key is not set. Please configure it in the settings.");
            }
            
            var apiUrl = _appState.GroqApiUrl;
            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                throw new InvalidOperationException("Groq API URL is not set. Please configure it in the settings.");
            }

            bool isReasoningModel = _appState.GroqModel.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase) || 
                                    _appState.GroqModel.StartsWith("o1-", StringComparison.OrdinalIgnoreCase);

            var payload = new ChatRequest
            {
                model = _appState.GroqModel,
                messages = new[]
                {
                    new ChatMessage { role = "user", content = prompt }
                },
                temperature = isReasoningModel ? 1.0 : 0.2,
                top_p = isReasoningModel ? 1.0 : null,
                reasoning_effort = isReasoningModel ? "medium" : null
            };

            if (isReasoningModel)
            {
                payload.max_completion_tokens = _appState.GroqMaxOutputTokens == 0 ? 8192 : _appState.GroqMaxOutputTokens;
            }
            else
            {
                payload.max_tokens = _appState.GroqMaxOutputTokens;
            }

            var json = JsonSerializer.Serialize(payload, JsonOpts);

            var requestUri = new Uri(new Uri(apiUrl), "chat/completions");
            using var req = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var client = _httpClientFactory.CreateClient("groq");
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"Groq chat/completions error {(int)resp.StatusCode}: {body}");
            }

            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            var result = await JsonSerializer.DeserializeAsync<ChatResponse>(s, JsonOpts, ct);

            var finalResponse = result?.choices != null && result.choices.Length > 0
                ? result.choices[0].message?.content ?? string.Empty
                : string.Empty;

            _logService.LogAi("Groq", _appState.GroqModel, prompt, finalResponse);

            return finalResponse;
        }

        private sealed class ChatRequest
        {
            public string model { get; set; } = string.Empty;
            public ChatMessage[] messages { get; set; } = Array.Empty<ChatMessage>();
            public double? temperature { get; set; }
            public int? max_tokens { get; set; }
            [JsonPropertyName("max_completion_tokens")]
            public int? max_completion_tokens { get; set; }
            [JsonPropertyName("reasoning_effort")]
            public string? reasoning_effort { get; set; }
            public double? top_p { get; set; }
        }

        private sealed class ChatMessage
        {
            public string role { get; set; } = "user";
            public string content { get; set; } = string.Empty;
        }

        private sealed class ChatResponse
        {
            public string? id { get; set; }
            public string? @object { get; set; }
            public long created { get; set; }
            public string? model { get; set; }
            public ChatChoice[] choices { get; set; } = Array.Empty<ChatChoice>();
        }

        private sealed class ChatChoice
        {
            public int index { get; set; }
            public ChatMessage? message { get; set; }
            [JsonPropertyName("finish_reason")]
            public string? finish_reason { get; set; }
        }
    }
}