using System.Net.Http;
using System.Net.Http.Json;
using LlmContextCollector.Models;
using LlmContextCollector.Services;

namespace LlmContextCollector.AI
{
    public class OllamaProvider : ITextGenerationProvider
    {
        private readonly HttpClient _httpClient;
        private readonly AiModelConfig _config;
        private readonly AppLogService _logService;

        public OllamaProvider(HttpClient httpClient, AiModelConfig config, AppLogService logService)
        {
            _httpClient = httpClient;
            _config = config;
            _logService = logService;
        }

        public async Task<string> GenerateAsync(string prompt, IEnumerable<AttachedImage>? images = null, CancellationToken ct = default)
        {
            var baseUrl = _config.ApiUrl.TrimEnd('/');
            
            var userMessage = new 
            { 
                role = "user", 
                content = prompt,
                images = images?.Select(img => {
                    var parts = img.Base64Thumbnail.Split(',');
                    return parts.Length > 1 ? parts[1] : parts[0];
                }).ToArray()
            };

            var requestBody = new
            {
                model = _config.ModelName,
                messages = new[] { userMessage },
                stream = false,
                options = new { num_ctx = 32768 }
            };

            using var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/chat/completions", requestBody, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaResponse>(cancellationToken: ct);
            var finalResponse = result?.Choices?[0]?.Message?.Content ?? string.Empty;

            _logService.LogAi("Ollama", _config.ModelName, prompt, finalResponse);
            return finalResponse;
        }

        private class OllamaResponse
        {
            public Choice[]? Choices { get; set; }
        }
        private class Choice
        {
            public Message? Message { get; set; }
        }
        private class Message
        {
            public string? Content { get; set; }
        }
    }
}