using LlmContextCollector.Models;

namespace LlmContextCollector.AI
{
    public interface ITextGenerationProvider
    {
        Task<string> GenerateAsync(string prompt, IEnumerable<AttachedImage>? images = null, CancellationToken ct = default);
        IAsyncEnumerable<string> GenerateStreamAsync(string prompt, IEnumerable<AttachedImage>? images = null, CancellationToken ct = default);
    }
}