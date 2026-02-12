using LlmContextCollector.Services;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace LlmContextCollector.Services
{
    public class OllamaService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AppState _appState;
        private readonly AiLogService _aiLogService;

        public OllamaService(IHttpClientFactory httpClientFactory, AppState appState, AiLogService aiLogService)
        {
            _httpClientFactory = httpClientFactory;
            _appState = appState;
            _aiLogService = aiLogService;
        }

        public async IAsyncEnumerable<string> GetChatResponseStreamAsync(IEnumerable<object> messages, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var requestBody = new
            {
                model = _appState.OllamaModel,
                messages = messages,
                stream = true,
                keep_alive = 0,
                options = new
                {
                    num_ctx = 65536
                }
            };

            var client = _httpClientFactory.CreateClient("OllamaClient");
            var baseUrl = _appState.OllamaApiUrl.TrimEnd('/');

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
            {
                Content = JsonContent.Create(requestBody)
            };

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
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
                if (string.IsNullOrEmpty(jsonData)) continue;

                string? content = null;
                try
                {
                    using var doc = JsonDocument.Parse(jsonData);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                        choices.GetArrayLength() > 0 &&
                        choices[0].TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("content", out var contentProp))
                    {
                        content = contentProp.GetString();
                    }
                    else if (doc.RootElement.TryGetProperty("message", out var msg) &&
                             msg.TryGetProperty("content", out var msgContent))
                    {
                        content = msgContent.GetString();
                    }
                }
                catch { }

                if (!string.IsNullOrEmpty(content))
                {
                    yield return content;
                }
            }
        }

        public async IAsyncEnumerable<string> GetAiResponseStream(string userPrompt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var messages = new[] { new { role = "user", content = userPrompt } };
            await foreach (var token in GetChatResponseStreamAsync(messages, ct))
            {
                yield return token;
            }
        }

        public async Task<string> GenerateContentAsync(string prompt, CancellationToken ct = default)
        {
            var sb = new StringBuilder();
            await foreach (var token in GetAiResponseStream(prompt, ct))
            {
                sb.Append(token);
            }

            var finalResponse = sb.ToString();
            _aiLogService.Log("Ollama (Service Call)", _appState.OllamaModel, prompt, finalResponse);

            return finalResponse;
        }
    }
}