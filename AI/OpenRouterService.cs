using LlmContextCollector.Components.Pages.HomePanels;
using LlmContextCollector.Models;
using LlmContextCollector.Services;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace LlmContextCollector.AI
{
    public class OpenRouterService
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AppState _appState;
        private readonly ContextProcessingService _contextProcessingService;

        public OpenRouterService(
            IHttpClientFactory httpClientFactory,
            AppState appState,
            ContextProcessingService contextProcessingService)
        {
            _httpClientFactory = httpClientFactory;
            _appState = appState;
            _contextProcessingService = contextProcessingService;
        }

        public async Task<DiffResultArgs> GenerateDiffFromContextAsync(IEnumerable<string> sortedFilePaths)
        {
            var apiKey = _appState.OpenRouterApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("OpenRouter API key is not set. Please configure it in the settings.");
            }

            var contextString = await _contextProcessingService.BuildContextForClipboardAsync(true, true, sortedFilePaths);

            if (string.IsNullOrWhiteSpace(contextString))
            {
                return new DiffResultArgs("The context is empty. Please add files or a prompt.", new List<DiffResult>(), "");
            }

            var payload = new ChatRequest
            {
                Model = _appState.OpenRouterModel,
                Messages = new[]
                {
                    new ChatMessage { Role = "user", Content = contextString }
                }
            };

            var client = _httpClientFactory.CreateClient("OpenRouter");
            using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions");

            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            if (!string.IsNullOrWhiteSpace(_appState.OpenRouterSiteUrl))
            {
                req.Headers.Add("HTTP-Referer", _appState.OpenRouterSiteUrl);
            }
            if (!string.IsNullOrWhiteSpace(_appState.OpenRouterSiteName))
            {
                req.Headers.Add("X-Title", _appState.OpenRouterSiteName);
            }

            req.Content = JsonContent.Create(payload, options: JsonOpts);

            using var resp = await client.SendAsync(req);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"OpenRouter API error {(int)resp.StatusCode}: {body}");
            }

            var result = await resp.Content.ReadFromJsonAsync<ChatResponse>(JsonOpts);
            var responseContent = result?.Choices != null && result.Choices.Length > 0
                ? result.Choices[0].Message?.Content ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(responseContent))
            {
                return new DiffResultArgs("The model returned an empty response.", new List<DiffResult>(), "");
            }

            return await _contextProcessingService.ProcessChangesFromClipboardAsync(responseContent);
        }

        private sealed class ChatRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; } = string.Empty;
            [JsonPropertyName("messages")]
            public ChatMessage[] Messages { get; set; } = Array.Empty<ChatMessage>();
        }

        private sealed class ChatMessage
        {
            [JsonPropertyName("role")]
            public string Role { get; set; } = "user";
            [JsonPropertyName("content")]
            public string Content { get; set; } = string.Empty;
        }

        private sealed class ChatResponse
        {
            [JsonPropertyName("choices")]
            public ChatChoice[] Choices { get; set; } = Array.Empty<ChatChoice>();
        }

        private sealed class ChatChoice
        {
            [JsonPropertyName("message")]
            public ChatMessage? Message { get; set; }
        }
    }
}