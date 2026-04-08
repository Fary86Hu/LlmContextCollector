namespace LlmContextCollector.Models
{
    public class Settings
    {
        public string Theme { get; set; } = "System";
        public string GroqApiKey { get; set; } = string.Empty;
        public string GroqModel { get; set; } = "openai/gpt-oss-120b";
        public int GroqMaxOutputTokens { get; set; } = 2048;
        public string GroqApiUrl { get; set; } = "https://api.groq.com/openai/v1/";
        public string OllamaApiUrl { get; set; } = "http://localhost:11434/v1/"; // Ez a chat API base URL
        public string OllamaModel { get; set; } = "qwen3:4b-instruct";
        public bool OllamaShowThinking { get; set; } = true;
        
        // --- Azure DevOps Beállítások ---
        public string AzureDevOpsOrganizationUrl { get; set; } = string.Empty;
        public string AzureDevOpsProject { get; set; } = string.Empty;
        public string AzureDevOpsIterationPath { get; set; } = string.Empty;
        public string AzureDevOpsPat { get; set; } = string.Empty;
        public bool AdoDownloadOnlyMine { get; set; } = false;

        // --- Build & Debug Beállítások ---
        public string BuildCommand { get; set; } = "dotnet build";
        public string RunCommand { get; set; } = "dotnet run";
    }
}