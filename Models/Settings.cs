using System;
using System.Collections.Generic;

namespace LlmContextCollector.Models
{
    public class Settings
    {
        public string Theme { get; set; } = "System";
        
        // Dinamikus modell lista
        public List<AiModelConfig> AiModels { get; set; } = new();
        
        // Feladathoz rendelt modellek ID-jai
        public Guid CommitMessageModelId { get; set; } = Guid.Empty;
        public Guid BranchNameModelId { get; set; } = Guid.Empty;

        // Chat specifikus fallback beállítások
        public string OllamaApiUrl { get; set; } = "http://localhost:11434/v1/";
        public string OllamaModel { get; set; } = "qwen2.5:7b-instruct";
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