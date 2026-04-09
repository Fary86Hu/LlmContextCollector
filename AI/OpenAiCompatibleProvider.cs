using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using LlmContextCollector.Models;
using LlmContextCollector.Services;

namespace LlmContextCollector.AI
{
    public sealed class OpenAiCompatibleProvider : ITextGenerationProvider
    {
        private readonly HttpClient _httpClient;
        private readonly AiModelConfig _config;
        private readonly AppLogService _logService;

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public OpenAiCompatibleProvider(HttpClient httpClient, AiModelConfig config, AppLogService logService)
        {
            _httpClient = httpClient;
            _config = config;
            _logService = logService;
        }

        public async Task<string> GenerateAsync(string prompt, IEnumerable<AttachedImage>? images = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_config.ApiUrl)) throw new InvalidOperationException("API URL nincs megadva.");

            object content;
            if (images == null || !images.Any())
            {
                content = prompt;
            }
            else
            {
                var contentList = new List<object> { new { type = "text", text = prompt } };
                foreach (var img in images)
                {
                    contentList.Add(new { type = "image_url", image_url = new { url = img.Base64Thumbnail } });
                }
                content = contentList.ToArray();
            }

            var payload = new 
            {
                model = _config.ModelName,
                messages = new[] { new { role = "user", content = content } },
                max_tokens = _config.MaxOutputTokens == 0 ? 4096 : _config.MaxOutputTokens,
                temperature = 0.2
            };

            var json = JsonSerializer.Serialize(payload, JsonOpts);
            var requestUri = new Uri(new Uri(_config.ApiUrl.TrimEnd('/') + "/"), "chat/completions");

            using var req = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(_config.ApiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
            }

            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"API hiba ({_config.FriendlyName}): {(int)resp.StatusCode} - {body}");
            }

            var result = await resp.Content.ReadFromJsonAsync<ChatResponse>(JsonOpts, ct);
            var finalResponse = result?.Choices?[0].Message?.Content ?? string.Empty;

            _logService.LogAi(_config.FriendlyName, _config.ModelName, prompt, finalResponse);
            return finalResponse;
        }

        private class ChatResponse
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