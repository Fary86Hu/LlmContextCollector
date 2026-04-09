using System;

namespace LlmContextCollector.Models
{
    public enum AiProviderType
    {
        OpenAiCompatible,
        Ollama,
        Gemini
    }

    public class AiModelConfig
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string FriendlyName { get; set; } = "Új Modell";
        public AiProviderType ProviderType { get; set; } = AiProviderType.OpenAiCompatible;
        public string ApiUrl { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;
        public int MaxOutputTokens { get; set; } = 4096;

        public AiModelConfig Clone() => (AiModelConfig)this.MemberwiseClone();
    }
}