using LlmContextCollector.Models;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;

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

            // Regex a fájl blokkok megtalálására (Header + Code Block)
            // Elfogadja a ``` kiterjesztés formátumot és a blokk végét is
            var fileBlockRegex = new Regex(
                @"(?:^|\n)(?:Új Fájl|Fájl):\s*(?<path>[^\r\n]+)\s*```[a-zA-Z0-9]*\r?\n(?<code>.*?)(?:\r?\n?```(?=\s*(?:(?:\r?\n){2,}|$|Új Fájl|Fájl|\[CHANGE_LOG\]))|\z)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

            // Regex a [CHANGE_LOG] blokkok kinyerésére
            var changeLogRegex = new Regex(@"\[CHANGE_LOG\](.*?)\[/CHANGE_LOG\]", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

            var matches = fileBlockRegex.Matches(text);

            string globalExplanation = "";
            int lastMatchEndIndex = 0;

            // Az első találat előtti rész a Globális Magyarázat (kivéve, ha van benne CHANGE_LOG az első fájlhoz)
            if (matches.Count > 0)
            {
                var firstMatchIndex = matches[0].Index;
                var preText = text.Substring(0, firstMatchIndex);

                // Megnézzük, van-e CHANGE_LOG a bevezetőben, ami az első fájlhoz tartozik
                var logMatch = changeLogRegex.Match(preText);
                if (logMatch.Success)
                {
                    // Ha van, akkor a logMatch előtti rész a globális magyarázat
                    globalExplanation = preText.Substring(0, logMatch.Index).Trim();
                    // A logMatch tartalmát majd az első fájlhoz rendeljük hozzá a ciklusban
                }
                else
                {
                    globalExplanation = preText.Trim();
                }
            }
            else
            {
                globalExplanation = text.Trim();
            }

            for (int i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                var path = match.Groups["path"].Value.Trim().Replace('\\', '/');
                var code = match.Groups["code"].Value.Trim(); // Nem kell a ``` már, mert a regex csoportba van

                // Státusz meghatározása
                var status = match.Value.TrimStart().StartsWith("Új", StringComparison.OrdinalIgnoreCase)
                             ? DiffStatus.New
                             : DiffStatus.Modified;

                // Explanation keresése
                // Két helyen lehet:
                // 1. Közvetlenül a fájl blokk előtt (a szövegrész az előző match vége és a mostani match eleje között)
                string explanation = "";

                // A keresési tartomány kezdete: az előző blokk vége (vagy 0, ha ez az első)
                int searchStart = (i == 0) ? 0 : matches[i - 1].Index + matches[i - 1].Length;
                // A keresési tartomány vége: a mostani blokk kezdete
                int searchEnd = match.Index;

                if (searchEnd > searchStart)
                {
                    string gapText = text.Substring(searchStart, searchEnd - searchStart);
                    var logMatch = changeLogRegex.Match(gapText);
                    if (logMatch.Success)
                    {
                        explanation = logMatch.Groups[1].Value.Trim();
                    }
                    else
                    {
                        // Ha nincs explicit tag, de van szöveg a két blokk között, és nem csak whitespace
                        // akkor azt tekintjük magyarázatnak (fallback), de csak ha nem az első elem (ott a global expl van)
                        if (i > 0 && !string.IsNullOrWhiteSpace(gapText))
                        {
                            explanation = gapText.Trim();
                        }
                    }
                }

                parsedFiles.Add(new ParsedFile
                {
                    Path = path,
                    NewContent = code,
                    Status = status,
                    Explanation = explanation
                });
            }

            return (globalExplanation, parsedFiles);
        }
    }
}