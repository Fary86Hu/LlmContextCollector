namespace LlmContextCollector.AI
{
    public interface ITextGenerationProvider
    {
        Task<string> GenerateAsync(string prompt, CancellationToken ct = default);
    }
}