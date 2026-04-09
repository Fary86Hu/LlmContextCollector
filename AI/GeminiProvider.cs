using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlmContextCollector.Models;
using LlmContextCollector.Services;

namespace LlmContextCollector.AI
{
    public class GeminiProvider : ITextGenerationProvider
    {
        private readonly HttpClient _httpClient;
        private readonly AiModelConfig _config;
        private readonly AppLogService _logService;

        public GeminiProvider(HttpClient httpClient, AiModelConfig config, AppLogService logService)
        {
            _httpClient = httpClient;
            _config = config;
            _logService = logService;
        }

        public async Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
        {
            var apiKey = (_config.ApiKey ?? string.Empty).Trim();
            var modelName = string.IsNullOrWhiteSpace(_config.ModelName) ? "gemini-3-flash" : _config.ModelName.Trim();

            // 2026-os stabil végpont
            var url = $"https://generativelanguage.googleapis.com/v1/models/{modelName}:generateContent?key={apiKey}";

            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[] { new { text = prompt } }
                    }
                },
                generationConfig = new
                {
                    maxOutputTokens = _config.MaxOutputTokens <= 0 ? 8192 : _config.MaxOutputTokens,
                    temperature = 0.2
                }
            };

            using var response = await _httpClient.PostAsJsonAsync(url, requestBody, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                // Fallback a v1beta végpontra, ha a v1 valamiért nem érné el az adott modellt
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    var backupUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={apiKey}";
                    using var backupResponse = await _httpClient.PostAsJsonAsync(backupUrl, requestBody, ct);
                    if (backupResponse.IsSuccessStatusCode)
                    {
                        return await ParseGeminiResponse(backupResponse, modelName, prompt, ct);
                    }
                }
                throw new InvalidOperationException($"Gemini API hiba ({response.StatusCode}): {errorBody}");
            }

            return await ParseGeminiResponse(response, modelName, prompt, ct);
        }

        private async Task<string> ParseGeminiResponse(HttpResponseMessage response, string model, string prompt, CancellationToken ct)
        {
            var result = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken: ct);
            var finalResponse = result?.Candidates?[0]?.Content?.Parts?[0]?.Text ?? string.Empty;

            _logService.LogAi("Gemini", model, prompt, finalResponse);
            return finalResponse;
        }

        private class GeminiResponse
        {
            [JsonPropertyName("candidates")]
            public Candidate[]? Candidates { get; set; }
        }

        private class Candidate
        {
            [JsonPropertyName("content")]
            public Content? Content { get; set; }
        }

        private class Content
        {
            [JsonPropertyName("parts")]
            public Part[]? Parts { get; set; }
        }

        private class Part
        {
            [JsonPropertyName("text")]
            public string? Text { get; set; }
        }
    }
}