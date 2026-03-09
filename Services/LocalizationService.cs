using System.Xml;
using System.Xml.Linq;
using LlmContextCollector.Models;

namespace LlmContextCollector.Services
{
    public class LocalizationService
    {
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