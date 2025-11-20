using LlmContextCollector.AI;
using LlmContextCollector.Models;
using LlmContextCollector.Utils;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

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
            var maxRequestTokens = 8192;
            var maxOutputTokens = _appState.GroqMaxOutputTokens;
            var charsPerToken = 3.5;
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

            sb.AppendLine("Based on the following git diff summary, please suggest a git branch name and a conventional commit message.");
            sb.AppendLine("The input is structured to show the code hierarchy (e.g. Namespace > Class > Method) for context.");
            sb.AppendLine("For CSS files, only the file status is provided.");
            sb.AppendLine("Format:");
            sb.AppendLine("[BRANCH]");
            sb.AppendLine("type/name");
            sb.AppendLine("[COMMIT]");
            sb.AppendLine("type: message");
            sb.AppendLine();
            sb.AppendLine("--- SUMMARY OF AFFECTED FILES ---");
            foreach (var d in diffs)
            {
                sb.AppendLine($"- {d.Status}: {d.Path}");
            }
            sb.AppendLine("--- END SUMMARY ---");
            sb.AppendLine();

            sb.AppendLine("--- DETAILED CHANGES ---");

            var headerLen = sb.Length;
            var inputBudgetTokens = Math.Max(256, maxRequestTokens - maxOutputTokens);
            var inputBudgetChars = (int)(inputBudgetTokens * charsPerToken);
            var remaining = Math.Max(0, inputBudgetChars - headerLen - reserve);

            var maxCharsPerFile = 2500;

            foreach (var diff in diffs)
            {
                if (remaining <= 0) break;

                string sectionContent;
                string ext = Path.GetExtension(diff.Path).ToLowerInvariant();
                bool isStructureSupported = ext == ".cs" || ext == ".razor" || ext == ".cshtml" || 
                                            ext == ".js" || ext == ".ts" || ext == ".tsx" || ext == ".jsx";
                bool isCss = ext == ".css" || ext == ".scss" || ext == ".less" || ext == ".sass";

                if (isCss)
                {
                    sectionContent = $"(CSS/Style definitions {diff.Status.ToString().ToLower()})";
                }
                else if (isStructureSupported)
                {
                    if (diff.Status == DiffStatus.New)
                    {
                        sectionContent = CodeStructureDiffHelper.GetFileStructure(diff.NewContent, ext);
                    }
                    else if (diff.Status == DiffStatus.Modified || diff.Status == DiffStatus.NewFromModified)
                    {
                        sectionContent = CodeStructureDiffHelper.GetContextualDiff(diff.OldContent, diff.NewContent, ext);
                    }
                    else
                    {
                        sectionContent = DiffPatcher.CreateUnifiedDiff(diff.OldContent, diff.NewContent);
                    }
                }
                else
                {
                    sectionContent = DiffPatcher.CreateUnifiedDiff(diff.OldContent, diff.NewContent, contextLines: 2);
                }

                if (sectionContent.Length > maxCharsPerFile)
                {
                    sectionContent = sectionContent.Substring(0, maxCharsPerFile) + "\n... (content truncated) ...\n";
                }

                var fileBlock = $"\n--- File: {diff.Path} ({diff.Status}) ---\n{sectionContent}";

                if (fileBlock.Length <= remaining)
                {
                    sb.Append(fileBlock);
                    remaining -= fileBlock.Length;
                }
                else
                {
                    sb.Append(TruncateWithNotice(fileBlock, remaining, "[...global limit reached...]"));
                    remaining = 0;
                    break;
                }
            }

            return sb.ToString();
        }

        private static string TruncateWithNotice(string text, int limit, string notice)
        {
            if (limit <= 0) return "\n" + notice + "\n";
            if (text.Length <= limit) return text;
            var cut = Math.Min(limit, text.Length);
            return text.Substring(0, cut) + "\n" + notice + "\n";
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

    public static class CodeStructureDiffHelper
    {
        private static readonly Regex CsNamespaceRegex = new(@"^\s*namespace\s+([\w\.]+)", RegexOptions.Compiled);
        private static readonly Regex CsClassRegex = new(@"^\s*(?:public|internal|private|protected|static|sealed|abstract|partial|\s)*\s*(class|interface|struct|record|enum)\s+([\w<>]+)", RegexOptions.Compiled);
        private static readonly Regex CsMethodRegex = new(@"^\s*(?:public|internal|private|protected|static|async|virtual|override|new|extern|readonly|\s)*\s*[\w<>[\]?]+\s+(\w+)\s*\(", RegexOptions.Compiled);
        private static readonly Regex CsConstructorRegex = new(@"^\s*(?:public|internal|private|protected|static|\s)*\s*(\w+)\s*\(", RegexOptions.Compiled);
        
        private static readonly Regex JsDefRegex = new(@"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?(?:function\s+|class\s+)(\w+)", RegexOptions.Compiled);
        private static readonly Regex JsVarRegex = new(@"^\s*(?:export\s+)?(?:const|let|var)\s+(\w+)\s*=", RegexOptions.Compiled);
        private static readonly Regex JsMethodRegex = new(@"^\s*(?:private|public|protected|static|async|\s)*\s*(\w+)\s*\(", RegexOptions.Compiled);

        private static readonly Regex RazorDirectiveRegex = new(@"^@(page|inject|layout|inherits)\s+", RegexOptions.Compiled);
        private static readonly Regex RazorCodeBlockRegex = new(@"^@(code|functions)\s*", RegexOptions.Compiled);

        public static string GetFileStructure(string content, string extension)
        {
            var sb = new StringBuilder();
            var lines = content.Split('\n');

            foreach (var line in lines)
            {
                if (IsDefinitionLine(line, extension))
                {
                    var displayLine = line.TrimEnd().TrimEnd('{').TrimEnd();
                    sb.AppendLine(displayLine);
                }
            }

            if (sb.Length == 0) return "(Empty or non-structural file)";
            return sb.ToString();
        }

        public static string GetContextualDiff(string oldContent, string newContent, string extension)
        {
            var oldLines = oldContent.Replace("\r\n", "\n").Split('\n');
            var newLines = newContent.Replace("\r\n", "\n").Split('\n');
            var opcodes = DiffUtility.GetOpcodes(oldLines, newLines);

            var sb = new StringBuilder();

            foreach (var op in opcodes)
            {
                if (op.Tag == 'e') continue;

                var contextStack = GetContextHierarchy(oldLines, op.I1, extension);
                
                if (contextStack.Any())
                {
                    sb.AppendLine();
                    foreach (var ctx in contextStack)
                    {
                        sb.AppendLine(ctx);
                    }
                }
                else
                {
                    sb.AppendLine("\n[Global scope / Unknown context]");
                }

                sb.AppendLine("{diff block}");
                if (op.Tag == 'd' || op.Tag == 'r')
                {
                    for (int i = op.I1; i < op.I2; i++)
                        sb.AppendLine($"   - {oldLines[i].Trim()}");
                }
                if (op.Tag == 'i' || op.Tag == 'r')
                {
                    for (int j = op.J1; j < op.J2; j++)
                        sb.AppendLine($"   + {newLines[j].Trim()}");
                }
            }

            return sb.ToString();
        }

        private static bool IsDefinitionLine(string line, string extension)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            var trimmed = line.Trim();
            if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith("*")) return false;

            bool isCs = extension == ".cs";
            bool isRazor = extension == ".razor" || extension == ".cshtml";
            bool isJsTs = extension == ".js" || extension == ".ts" || extension == ".tsx" || extension == ".jsx";

            if (isCs || isRazor)
            {
                if (CsNamespaceRegex.IsMatch(line) ||
                    CsClassRegex.IsMatch(line) ||
                    CsMethodRegex.IsMatch(line) ||
                    CsConstructorRegex.IsMatch(line))
                {
                    return true;
                }
                
                if (isRazor)
                {
                    if (RazorDirectiveRegex.IsMatch(line) || RazorCodeBlockRegex.IsMatch(line)) return true;
                }
            }

            if (isJsTs || isRazor) 
            {
                if (JsDefRegex.IsMatch(line) || JsVarRegex.IsMatch(line)) return true;
                if (JsMethodRegex.IsMatch(line) && !trimmed.StartsWith("if") && !trimmed.StartsWith("for") && !trimmed.StartsWith("while") && !trimmed.StartsWith("switch") && !trimmed.StartsWith("catch"))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<string> GetContextHierarchy(string[] lines, int changeStartLine, string extension)
        {
            var hierarchy = new List<string>();
            int currentIndent = int.MaxValue;

            for (int i = changeStartLine - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                int indent = line.TakeWhile(char.IsWhiteSpace).Count();

                if (indent < currentIndent)
                {
                    if (IsDefinitionLine(line, extension))
                    {
                        var display = line.TrimEnd().TrimEnd('{').TrimEnd();
                        hierarchy.Insert(0, display);
                        currentIndent = indent;
                    }
                }

                if (currentIndent == 0) break;
                if (changeStartLine - i > 300) break;
            }

            return hierarchy;
        }
    }

    public static class DiffPatcher
    {
        public static string CreateUnifiedDiff(string oldText, string newText, int contextLines = 3)
        {
            var oldLines = oldText.Replace("\r\n", "\n").Split('\n');
            var newLines = newText.Replace("\r\n", "\n").Split('\n');
            var opcodes = DiffUtility.GetOpcodes(oldLines, newLines);

            var sb = new StringBuilder();
            
            for (int opIdx = 0; opIdx < opcodes.Count; opIdx++)
            {
                var op = opcodes[opIdx];
                
                switch (op.Tag)
                {
                    case 'e':
                        if (op.I2 - op.I1 <= contextLines * 2) {
                             for (int i = op.I1; i < op.I2; i++) sb.AppendLine($"  {oldLines[i]}");
                        } else {
                             if (opIdx > 0) for (int i = 0; i < contextLines; i++) sb.AppendLine($"  {oldLines[op.I1 + i]}");
                             if (opIdx > 0 && opIdx < opcodes.Count - 1) sb.AppendLine("  ...");
                             if (opIdx < opcodes.Count - 1) for (int i = 0; i < contextLines; i++) sb.AppendLine($"  {oldLines[op.I2 - contextLines + i]}");
                        }
                        break;
                    case 'd':
                        for (int i = op.I1; i < op.I2; i++) sb.AppendLine($"- {oldLines[i]}");
                        break;
                    case 'i':
                        for (int j = op.J1; j < op.J2; j++) sb.AppendLine($"+ {newLines[j]}");
                        break;
                    case 'r':
                        for (int i = op.I1; i < op.I2; i++) sb.AppendLine($"- {oldLines[i]}");
                        for (int j = op.J1; j < op.J2; j++) sb.AppendLine($"+ {newLines[j]}");
                        break;
                }
            }
            return sb.ToString();
        }
    }
}