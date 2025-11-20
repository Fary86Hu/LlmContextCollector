namespace LlmContextCollector.Models
{
    // A teljes, JSON fájlban tárolt struktúrát reprezentálja
    public class PromptData
    {
        public GlobalPreferences Preferences { get; set; } = new();
        public List<PromptTemplate> Prompts { get; set; } = new();
    }

    public class GlobalPreferences
    {
        // Egyesített rendszer prompt
        public string SystemPrompt { get; set; } = string.Empty;
    }
}