using System;
using System.Collections.Generic;

namespace LlmContextCollector.Models
{
    public class Settings
    {
        public string Theme { get; set; } = "System";
        
        public List<AiModelConfig> AiModels { get; set; } = new();
        
        public Guid GitSuggestionModelId { get; set; } = Guid.Empty;
        public Guid ChatModelId { get; set; } = Guid.Empty;
        public Guid AgentModelId { get; set; } = Guid.Empty;

        public string OllamaApiUrl { get; set; } = "http://localhost:11434/v1/";
        public string OllamaModel { get; set; } = "qwen2.5:7b-instruct";
        public bool OllamaShowThinking { get; set; } = true;

        public string AzureDevOpsOrganizationUrl { get; set; } = string.Empty;
        public string AzureDevOpsProject { get; set; } = string.Empty;
        public string AzureDevOpsIterationPath { get; set; } = string.Empty;
        public string AzureDevOpsPat { get; set; } = string.Empty;
        public bool AdoDownloadOnlyMine { get; set; } = false;

        public string BuildCommand { get; set; } = "dotnet build";
        public string RunCommand { get; set; } = "dotnet run";
    }
}