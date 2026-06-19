using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using LlmContextCollector.Models;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

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
                    foreach (Match match in matches) foundKeys.Add(match.Groups["key"].Value);
                }
            }

            if (!foundKeys.Any()) return results;

            var ext = Path.GetExtension(resxPath).ToLower();
            if (ext == ".json")
            {
                var content = await File.ReadAllTextAsync(resxPath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(content) ?? new();
                var serializeOptions = new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                foreach (var key in foundKeys)
                {
                    if (dict.TryGetValue(key, out var vals))
                        results.Add(new DiffResult { Path = $"[LOC] {key}", NewContent = JsonSerializer.Serialize(vals, serializeOptions), Status = DiffStatus.New, IsSelectedForAccept = true });
                }
            }
            else
            {
                XDocument doc = XDocument.Load(resxPath);
                var entries = doc.Root?.Elements("data").ToDictionary(e => e.Attribute("name")?.Value ?? "", e => e.Element("value")?.Value ?? "") ?? new();
                foreach (var key in foundKeys)
                {
                    if (entries.TryGetValue(key, out var val))
                        results.Add(new DiffResult { Path = $"[LOC] {key}", NewContent = val, Status = DiffStatus.New, IsSelectedForAccept = true });
                }
            }
            return results;
        }

        public async Task<int> UpdateResourceFileAsync(string filePath, string localizationXml)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return 0;

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var isJson = Path.GetExtension(filePath).ToLower() == ".json";

            if (!File.Exists(filePath))
            {
                if (isJson)
                {
                    await File.WriteAllTextAsync(filePath, "{}");
                }
                else
                {
                    await File.WriteAllTextAsync(filePath, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n</root>");
                }
            }

            if (isJson)
            {
                var content = await File.ReadAllTextAsync(filePath);
                Dictionary<string, Dictionary<string, string>> dict;
                try
                {
                    dict = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(content) ?? new();
                }
                catch
                {
                    dict = new();
                }

                var newItems = ParseLocalizationTags(localizationXml);
                int added = 0;
                foreach (var item in newItems)
                {
                    var key = item.Attribute("name")?.Value ?? "";
                    var val = item.Element("value")?.Value ?? "";
                    try { dict[key] = JsonSerializer.Deserialize<Dictionary<string, string>>(val) ?? new(); }
                    catch { dict[key] = new Dictionary<string, string> { { "en-US", val }, { "hu-HU", val } }; }
                    added++;
                }
                var serializeOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(dict, serializeOptions));
                return added;
            }
            else
            {
                return await UpdateResxFileAsync(filePath, localizationXml);
            }
        }

        private async Task<int> UpdateResxFileAsync(string filePath, string xml)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(filePath);
            }
            catch
            {
                doc = XDocument.Parse("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n</root>");
            }

            if (doc.Root == null)
            {
                doc = XDocument.Parse("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n</root>");
            }

            var newElements = ParseLocalizationTags(xml);
            int added = 0;
            foreach (var el in newElements)
            {
                var name = el.Attribute("name")?.Value;
                if (doc.Root?.Elements("data").Any(e => e.Attribute("name")?.Value == name) == false)
                {
                    doc.Root.Add(el);
                    added++;
                }
            }
            doc.Save(filePath);
            return added;
        }

        private List<XElement> ParseLocalizationTags(string xmlFragment)
        {
            var results = new List<XElement>();
            var regex = new Regex(@"<data name=""(?<name>[^""]+)""[^>]*>\s*<value>(?<value>[\s\S]*?)<\/value>\s*</data>", RegexOptions.IgnoreCase);
            foreach (Match match in regex.Matches(xmlFragment))
            {
                results.Add(new XElement("data", new XAttribute("name", match.Groups["name"].Value), new XElement("value", match.Groups["value"].Value)));
            }
            return results;
        }
    }
}