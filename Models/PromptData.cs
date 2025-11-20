namespace LlmContextCollector.Models
{
    public class PromptData
    {
        public GlobalPreferences Preferences { get; set; } = new();
        public List<PromptTemplate> Prompts { get; set; } = new();
    }

    public class GlobalPreferences
    {
        public string SystemPrompt { get; set; } = string.Empty;
    }
}