using LlmContextCollector.Models;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System;

namespace LlmContextCollector.Services
{
    public class LlmResponseParserService
    {
        public class ParsedFile
        {
            public string Path { get; set; } = "";
            public string NewContent { get; set; } = "";
            public DiffStatus Status { get; set; }
            public string Explanation { get; set; } = "";
        }

        public (string GlobalExplanation, List<ParsedFile> ParsedFiles) ParseResponse(string text)
        {
            var parsedFiles = new List<ParsedFile>();

            // Csak a fejléceket keressük Regex-szel, ez gyors és biztonságos
            var headerRegex = new Regex(@"(?:^|\n)(?<type>Új Fájl|Fájl):\s*(?<path>[^\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var matches = headerRegex.Matches(text);

            string globalExplanation = "";
            string nextFileExplanation = "";

            // 1. Globális magyarázat kinyerése (az első fájl előtt)
            if (matches.Count > 0)
            {
                var preamble = text.Substring(0, matches[0].Index);
                (globalExplanation, nextFileExplanation) = ExtractExplanationAndLog(preamble);
            }
            else
            {
                globalExplanation = text.Trim();
            }

            // 2. Fájlok feldolgozása
            for (int i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                var typeStr = match.Groups["type"].Value;
                var path = match.Groups["path"].Value.Trim().Replace('\\', '/');
                var status = typeStr.StartsWith("Új", StringComparison.OrdinalIgnoreCase) ? DiffStatus.New : DiffStatus.Modified;

                // A tartalom kezdete a fejléc után
                int contentStart = match.Index + match.Length;

                // A tartalom vége a következő fejléc kezdete, vagy a szöveg vége
                int contentEnd = (i == matches.Count - 1) ? text.Length : matches[i + 1].Index;

                // Nyers blokk a két fejléc között
                string rawBlock = text.Substring(contentStart, contentEnd - contentStart);

                // Szétválasztjuk a kódot és a következő fájlhoz tartozó log-ot (ha van a blokk végén)
                var (codePart, nextLog) = ExtractExplanationAndLog(rawBlock, looksForLogAtEnd: true);

                // Markdown tisztítás (ha van ``` keret)
                string cleanCode = RemoveMarkdownFences(codePart);

                parsedFiles.Add(new ParsedFile
                {
                    Path = path,
                    NewContent = cleanCode,
                    Status = status,
                    Explanation = nextFileExplanation // Az előző iterációból (vagy preamble-ből) jött log
                });

                // A most talált log a következő fájlhoz tartozik
                nextFileExplanation = nextLog;
            }

            return (globalExplanation, parsedFiles);
        }

        private (string content, string log) ExtractExplanationAndLog(string text, bool looksForLogAtEnd = false)
        {
            var logStartMarker = "[CHANGE_LOG]";
            var logEndMarker = "[/CHANGE_LOG]";

            int logStart = text.LastIndexOf(logStartMarker, StringComparison.OrdinalIgnoreCase);
            int logEnd = text.LastIndexOf(logEndMarker, StringComparison.OrdinalIgnoreCase);

            if (logStart != -1 && logEnd != -1 && logEnd > logStart)
            {
                // Van log a szövegben
                string log = text.Substring(logStart + logStartMarker.Length, logEnd - (logStart + logStartMarker.Length)).Trim();

                // Ha a log a blokk végén van (tipikus eset: Fájl A kódja ... [CHANGE_LOG]...[/CHANGE_LOG] Fájl B)
                // Akkor a tartalom a log előtti rész.
                string content = text.Substring(0, logStart).Trim();

                return (content, log);
            }

            // Nincs log, az egész tartalom a content (kivéve ha preamble, ott fordítva lehetne, de a standard formátum szerint a log a fájlhoz kötődik)
            return (text.Trim(), "");
        }

        private string RemoveMarkdownFences(string code)
        {
            code = code.Trim();
            // Egyszerű ellenőrzés: ha ```-al kezdődik
            if (code.StartsWith("```"))
            {
                int firstNewLine = code.IndexOf('\n');
                if (firstNewLine != -1)
                {
                    // Levágjuk az első sort (```csharp)
                    code = code.Substring(firstNewLine + 1);

                    // Levágjuk az utolsó ```-t ha van
                    int lastFence = code.LastIndexOf("```");
                    if (lastFence != -1)
                    {
                        code = code.Substring(0, lastFence);
                    }
                }
            }
            return code.Trim();
        }
    }
}