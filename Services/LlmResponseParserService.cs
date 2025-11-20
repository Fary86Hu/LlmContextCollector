using LlmContextCollector.Models;
using System.Text.RegularExpressions;

namespace LlmContextCollector.Services
{
    public class LlmResponseParserService
    {
        public class ParsedFile
        {
            public string Path { get; set; } = "";
            public string NewContent { get; set; } = "";
            public DiffStatus Status { get; set; }
        }

        public (string GlobalExplanation, List<ParsedFile> ParsedFiles) ParseResponse(string text)
        {
            var parsedFiles = new List<ParsedFile>();
            
            // Módosított regex: A blokk végét jelző rész (?:\r?\n?```|\z) mostantól elfogadja
            // a szabályos lezárást VAGY a szöveg abszolút végét (\z) is.
            // Ez kezeli a csonkolt/befejezetlen LLM válaszokat az utolsó fájlnál.
            var fileBlockRegex = new Regex(
                @"^(?:Új Fájl|Fájl):\s*(?<path>[^\r\n]+)\s*```[a-zA-Z]*\r?\n(?<code>.*?)(?:\r?\n?```|\z)",
                RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.IgnoreCase);

            var firstMatch = fileBlockRegex.Match(text);
            string globalExplanation = firstMatch.Success ? text.Substring(0, firstMatch.Index).Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(globalExplanation) && !firstMatch.Success)
            {
                globalExplanation = text; // Assume everything is explanation if no code blocks found
            }

            var contentToParse = firstMatch.Success ? text.Substring(firstMatch.Index) : text;

            var matches = fileBlockRegex.Matches(contentToParse);
            foreach (Match match in matches.Cast<Match>())
            {
                parsedFiles.Add(new ParsedFile
                {
                    Path = match.Groups["path"].Value.Trim().Replace('\\', '/'),
                    NewContent = match.Groups["code"].Value.Trim(),
                    Status = match.Value.TrimStart().StartsWith("Új", StringComparison.OrdinalIgnoreCase)
                                 ? DiffStatus.New
                                 : DiffStatus.Modified
                });
            }

            return (globalExplanation, parsedFiles);
        }
    }
}