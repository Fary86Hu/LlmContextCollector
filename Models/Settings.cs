namespace LlmContextCollector.Models
{
    public class Settings
    {
        public string GroqApiKey { get; set; } = string.Empty;
        public string GroqModel { get; set; } = "llama-3.3-70b-versatile";
        public int GroqMaxOutputTokens { get; set; } = 2048;
        public string GroqApiUrl { get; set; } = "https://api.groq.com/openai/v1/";
    }
}