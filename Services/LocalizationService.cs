using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using LlmContextCollector.Models;

namespace LlmContextCollector.Services
{
    public class LocalizationService
    {
        private static readonly Regex[] LocalizationPatterns = new[]
        {
            new Regex(@"L\[""(?<key>[^""]+)""\]", RegexOptions.Compiled),
            new Regex(@"Localizer\[""(?<key>[^""]+)""\]", RegexOptions.Compiled),
            new Regex(@"\.GetLocalized\(""(?<key>[^""]+)""\)", RegexOptions.Compiled),
            new Regex(@"\[Display\(Name\s*=\s*""(?<key>[^""]+)""(?:,.*)?\)\]", RegexOptions.Compiled),
            new Regex(@"(?:Resources|Messages|Strings)\.(?<key>[a-zA-Z0-9_]+)", RegexOptions.Compiled)
        };

        public async Task<List<DiffResult>> ScanLocalizationsInFilesAsync(List<string> filePaths, string projectRoot, string resxPath)
        {
            var foundKeys = new HashSet<string>();
            var missingKeys = new List<string>();
            var results = new List<DiffResult>();

            if (string.IsNullOrEmpty(resxPath) || !File.Exists(resxPath)) return results;

            foreach (var relPath in filePaths)
            {
                var fullPath = Path.Combine(projectRoot, relPath);
                if (!File.Exists(fullPath)) continue;

                var content = await File.ReadAllTextAsync(fullPath);
                foreach (var pattern in LocalizationPatterns)
                {
                    var matches = pattern.Matches(content);
                    foreach (Match match in matches)
                    {
                        foundKeys.Add(match.Groups["key"].Value);
                    }
                }
            }

            if (!foundKeys.Any()) return results;

            XDocument doc;
            using (var stream = File.OpenRead(resxPath))
            {
                doc = XDocument.Load(stream);
            }

            var resxEntries = doc.Root?.Elements("data")
                .ToDictionary(
                    e => e.Attribute("name")?.Value ?? "",
                    e => e.Element("value")?.Value ?? ""
                ) ?? new Dictionary<string, string>();

            foreach (var key in foundKeys)
            {
                if (resxEntries.TryGetValue(key, out var val))
                {
                    results.Add(new DiffResult
                    {
                        Path = $"[LOC] {key}",
                        NewContent = val,
                        Status = DiffStatus.New,
                        IsSelectedForAccept = true,
                        Explanation = "Kinyert lokalizáció"
                    });
                }
                else
                {
                    missingKeys.Add(key);
                }
            }

            if (missingKeys.Any())
            {
                var distinctMissing = missingKeys.Distinct().OrderBy(k => k).ToList();
                var warning = $"Az alábbi kulcsok szerepelnek a kódban, de nem találhatók a resource fájlban:\n\n" + string.Join("\n", distinctMissing);
                _ = MainThread.InvokeOnMainThreadAsync(async () => {
                    if (Application.Current?.MainPage != null)
                        await Application.Current.MainPage.DisplayAlert("Hiányzó lokalizációk", warning, "OK");
                });
            }

            return results;
        }

        public async Task<int> UpdateResourceFileAsync(string filePath, string localizationXml)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("A megadott resource fájl nem található.");

            var newElements = ParseLocalizationTags(localizationXml);
            if (!newElements.Any()) return 0;

            XDocument doc;
            using (var stream = File.OpenRead(filePath))
            {
                doc = XDocument.Load(stream);
            }

            var root = doc.Root;
            if (root == null) throw new InvalidOperationException("Érvénytelen resource fájl formátum.");

            int addedCount = 0;
            foreach (var newElement in newElements)
            {
                var name = newElement.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(name)) continue;

                // Ellenőrizzük, létezik-e már ilyen nevű bejegyzés
                var existing = root.Elements("data")
                    .FirstOrDefault(e => e.Attribute("name")?.Value == name);

                if (existing != null)
                {
                    existing.SetElementValue("value", newElement.Element("value")?.Value ?? "");
                }
                else
                {
                    root.Add(newElement);
                    addedCount++;
                }
            }

            doc.Save(filePath);
            return addedCount;
        }

        private List<XElement> ParseLocalizationTags(string xmlFragment)
        {
            var results = new List<XElement>();
            // Mivel az LLM válaszban több <data> tag is lehet egymás után, egyenként dolgozzuk fel őket
            var regex = new System.Text.RegularExpressions.Regex(@"<data name=""(?<name>[^""]+)"" xml:space=""preserve"">\s*<value>(?<value>[\s\S]*?)<\/value>\s*</data>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            var matches = regex.Matches(xmlFragment);
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                try
                {
                    var element = new XElement("data",
                        new XAttribute("name", match.Groups["name"].Value),
                        new XAttribute(XNamespace.Xml + "space", "preserve"),
                        new XElement("value", match.Groups["value"].Value)
                    );
                    results.Add(element);
                }
                catch { }
            }

            return results;
        }
    }
}