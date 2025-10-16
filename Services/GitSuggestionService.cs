using LlmContextCollector.AI;
using LlmContextCollector.Models;
using LlmContextCollector.Utils;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace LlmContextCollector.Services
{
    public class GitSuggestionService
    {
        private readonly ITextGenerationProvider _generationProvider;
        private readonly AppState _appState;

        public GitSuggestionService(ITextGenerationProvider generationProvider, AppState appState)
        {
            _generationProvider = generationProvider;
            _appState = appState;
        }

        public async Task<(string? branch, string? commit)> GetSuggestionsAsync(List<DiffResult> diffs, string? globalExplanation = null, CancellationToken ct = default)
        {
            if (!diffs.Any())
            {
                return ("", "");
            }

            var prompt = BuildPrompt(diffs, globalExplanation);
            try
            {
                var llmResponse = await _generationProvider.GenerateAsync(prompt, ct);
                return ParseResponse(llmResponse);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LLM suggestion generation failed: {ex.Message}");
                return (null, null);
            }
        }

        private string BuildPrompt(List<DiffResult> diffs, string? globalExplanation)
        {
            var maxRequestTokens = 8192; // Use a reasonable default, as most models support at least 8k context.
            var maxOutputTokens = _appState.GroqMaxOutputTokens;
            var charsPerToken = 4.0;
            var reserve = 1024;
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(globalExplanation))
            {
                sb.AppendLine("Use the following explanation as the main context for the changes:");
                sb.AppendLine("--- GLOBAL EXPLANATION ---");
                sb.AppendLine(globalExplanation);
                sb.AppendLine("--- END GLOBAL EXPLANATION ---");
                sb.AppendLine();
            }

            sb.AppendLine("Based on the following git diff, please suggest a git branch name and a conventional commit message in English.");
            sb.AppendLine("The branch name should be in kebab-case, prefixed with 'feature/', 'fix/', 'chore/', etc.");
            sb.AppendLine("The commit message should follow the conventional commit format.");
            sb.AppendLine("Provide the response in the format: ");
            sb.AppendLine("[BRANCH]");
            sb.AppendLine("branch-name");
            sb.AppendLine();
            sb.AppendLine("[COMMIT]");
            sb.AppendLine("commit-message");
            sb.AppendLine();
            sb.AppendLine("--- GIT DIFF ---");

            var headerLen = sb.Length;
            var inputBudgetTokens = Math.Max(256, maxRequestTokens - maxOutputTokens);
            var inputBudgetChars = (int)(inputBudgetTokens * charsPerToken);
            var remaining = Math.Max(0, inputBudgetChars - headerLen - reserve);

            foreach (var diff in diffs)
            {
                if (remaining <= 0) break;

                var fileHeader = $"\n--- File: {diff.Path} ({diff.Status}) ---\n";
                var diffContent = DiffPatcher.CreateUnifiedDiff(diff.OldContent, diff.NewContent);
                var section = fileHeader + diffContent;

                if (section.Length <= remaining)
                {
                    sb.Append(section);
                    remaining -= section.Length;
                }
                else
                {
                    var truncated = TruncateWithNotice(section, remaining, $"[...truncated {diff.Path}...]");
                    sb.Append(truncated);
                    remaining -= truncated.Length;
                }
            }

            return sb.ToString();
        }

        private static string TruncateWithNotice(string text, int limit, string notice)
        {
            if (limit <= 0) return "\n" + notice + "\n";
            if (text.Length <= limit) return text;
            var cut = Math.Min(limit, text.Length);
            var nl = text.LastIndexOf('\n', cut - 1);
            if (nl <= 0) nl = cut;
            var prefix = text.Substring(0, nl);
            return prefix + "\n" + notice + "\n";
        }

        private (string branch, string commit) ParseResponse(string response)
        {
            var branchMatch = Regex.Match(response, @"\[BRANCH\]\s*([\s\S]*?)\s*(?:\[COMMIT\]|$)", RegexOptions.IgnoreCase);
            var commitMatch = Regex.Match(response, @"\[COMMIT\]\s*([\s\S]*)", RegexOptions.IgnoreCase);

            var branch = branchMatch.Success ? branchMatch.Groups[1].Value.Trim() : "suggestion-not-found";
            var commit = commitMatch.Success ? commitMatch.Groups[1].Value.Trim() : "Could not generate commit message.";

            return (branch, commit);
        }
    }

    public static class DiffPatcher
    {
        public static string CreateUnifiedDiff(string oldText, string newText)
        {
            var oldLines = oldText.Split('\n');
            var newLines = newText.Split('\n');
            var opcodes = DiffUtility.GetOpcodes(oldLines, newLines);

            var sb = new StringBuilder();
            foreach (var op in opcodes)
            {
                switch (op.Tag)
                {
                    case 'd':
                        for (int i = op.I1; i < op.I2; i++) sb.AppendLine($"- {oldLines[i].TrimEnd('\r')}");
                        break;
                    case 'i':
                        for (int j = op.J1; j < op.J2; j++) sb.AppendLine($"+ {newLines[j].TrimEnd('\r')}");
                        break;
                    case 'r':
                        for (int i = op.I1; i < op.I2; i++) sb.AppendLine($"- {oldLines[i].TrimEnd('\r')}");
                        for (int j = op.J1; j < op.J2; j++) sb.AppendLine($"+ {newLines[j].TrimEnd('\r')}");
                        break;
                }
            }
            return sb.ToString();
        }
    }
}