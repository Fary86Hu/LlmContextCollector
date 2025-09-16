namespace LlmContextCollector.AI
{
    public class DummyTextGenerationProvider : ITextGenerationProvider
    {
        public Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
        {
            // Egy valós implementáció itt hívná a helyi LLM-et.
            // Most egy előre definiált válasszal szimuláljuk a működést.
            string response = @"
[BRANCH]
feature/llm-git-suggestions

[COMMIT]
feat: Add LLM-based git suggestions

Implement a new feature to provide branch name and commit message suggestions based on the current git diff.
- A new service, GitSuggestionService, is created to handle the logic.
- The UI is updated to display the suggestions in the diff dialog.
";
            return Task.FromResult(response);
        }
    }
}