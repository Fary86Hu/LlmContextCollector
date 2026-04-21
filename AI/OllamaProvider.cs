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

        public async IAsyncEnumerable<string> GenerateStreamAsync(string prompt, IEnumerable<AttachedImage>? images = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
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
                stream = true,
                options = new { num_ctx = 65536 }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
            {
                Content = JsonContent.Create(requestBody)
            };

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                if (ct.IsCancellationRequested) break;
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;

                var jsonData = line.StartsWith("data: ") ? line.Substring(6).Trim() : line.Trim();
                if (jsonData == "[DONE]") break;

                string? content = null;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(jsonData);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                        choices.GetArrayLength() > 0 &&
                        choices[0].TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("content", out var contentProp))
                    {
                        content = contentProp.GetString();
                    }
                }
                catch { }

                if (!string.IsNullOrEmpty(content)) yield return content;
            }
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