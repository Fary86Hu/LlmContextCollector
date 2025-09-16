using System.Text;
using System.Text.RegularExpressions;

namespace LlmContextCollector.Services
{
    public class CodeStructureExtractor
    {
        // Reguláris kifejezések a C#-szerű nyelvek főbb elemeinek kinyerésére
        private static readonly RegexOptions RegexOpts = RegexOptions.Multiline | RegexOptions.Compiled;

        // XML doc kommentek (pl. /// <summary>...)
        private static readonly Regex DocCommentsRegex = new(@"^\s*///.*$", RegexOpts);
        private static readonly Regex UsingRegex = new(@"^\s*using\s+[\w\.]+;", RegexOpts);
        // Névtér
        private static readonly Regex NamespaceRegex = new(@"^\s*namespace\s+[\w\.]+", RegexOpts);
        // Osztály, interfész, struct, enum definíciók
        private static readonly Regex TypeDefinitionRegex = new(@"^\s*(?:public|internal|private|protected|static|sealed|abstract|partial)*\s*(class|interface|struct|enum)\s+\w+", RegexOpts);
        // Property-k (get/set-tel vagy anélkül)
        private static readonly Regex PropertyRegex = new(@"^\s*(?:public|internal|private|protected|static|virtual|override|new|readonly)*\s*[\w\.<>\[\],?]+\s+\w+\s*\{.*(get|set|=>|;)", RegexOpts);
        // Metódus aláírások (záró {, ; vagy =>)
        private static readonly Regex MethodRegex = new(@"^\s*(?:public|internal|private|protected|static|async|virtual|override|new|extern)*\s*[\w\.<>\[\],?]+\s+\w+\s*\(.*\)\s*(?:where\s+.*)?(?:{|;|=>)", RegexOpts);
        // CSS osztályok és ID-k
        private static readonly Regex CssClassAndIdRegex = new(@"(?<=[.#])[\w-]+", RegexOpts);


        public string ExtractStructure(string fileContent, string filePath)
        {
            var extension = Path.GetExtension(filePath)?.ToLowerInvariant();
            var sb = new StringBuilder();
            sb.AppendLine($"File: {filePath}");
            sb.AppendLine();

            switch (extension)
            {
                case ".cs":
                {
                    var structureSb = new StringBuilder();
                    var lines = fileContent.Split('\n');
                    foreach (var line in lines)
                    {
                        var trimmedLine = line.Trim();
                        if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith("//")) continue; // sima kommentek kihagyása

                        if (DocCommentsRegex.IsMatch(trimmedLine) ||
                            UsingRegex.IsMatch(trimmedLine) ||
                            NamespaceRegex.IsMatch(trimmedLine) ||
                            TypeDefinitionRegex.IsMatch(trimmedLine) ||
                            PropertyRegex.IsMatch(trimmedLine) ||
                            MethodRegex.IsMatch(trimmedLine))
                        {
                            structureSb.AppendLine(line); // Eredeti behúzás megtartása
                        }
                    }
                    if (structureSb.Length > 20) // Heurisztika: csak akkor használjuk ha találtunk valami értelmeset
                    {
                        sb.Append(structureSb.ToString());
                    }
                    else
                    {
                        sb.Append(fileContent); // Fallback if no structure found
                    }
                    break;
                }

                case ".css":
                {
                    var cssSb = new StringBuilder();
                    cssSb.AppendLine($"CSS File Name: {Path.GetFileName(filePath)}");
                    cssSb.AppendLine("Defined Classes and IDs:");

                    var matches = CssClassAndIdRegex.Matches(fileContent);
                    var uniqueNames = matches.Cast<Match>()
                                             .Select(m => m.Value)
                                             .Distinct(StringComparer.OrdinalIgnoreCase)
                                             .OrderBy(name => name)
                                             .ToList();
            
                    if (uniqueNames.Any())
                    {
                        foreach (var name in uniqueNames)
                        {
                            cssSb.AppendLine($"- {name}");
                        }
                    }
                    else
                    {
                        cssSb.AppendLine("(No classes or IDs found)");
                    }
                    sb.Append(cssSb.ToString());
                    break;
                }

                default:
                    // For unstructured files, just use the path and the full content
                    sb.Append(fileContent);
                    break;
            }

            return sb.ToString();
        }
    }
}