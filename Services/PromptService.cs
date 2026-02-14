using LlmContextCollector.Models;
using System.Text;

namespace LlmContextCollector.Services
{
    public class PromptService
    {
        private const string PromptFileName = ".llm_context_prompts.json";
        private readonly JsonStorageService _storage;
        private readonly string _promptsFolder;

        private PromptData _promptDataCache = new();

        // A gyári promptok nevei, amiket az Assetekből szinkronizálunk
        private readonly string[] _factoryPromptNames = { "Developer", "TaskReviewer", "Planner", "CodeReviewer" };

        public PromptService(JsonStorageService storage)
        {
            _storage = storage;
            // Felhasználói profil mappája: LlmContextCollector/prompts
            _promptsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LlmContextCollector", "prompts");

            if (!Directory.Exists(_promptsFolder))
            {
                Directory.CreateDirectory(_promptsFolder);
            }
        }

        private async Task EnsureLoadedAsync()
        {
            // 1. Preferenciák betöltése (aktív ID)
            var storedData = await _storage.ReadFromFileAsync<PromptData>(PromptFileName);
            if (storedData != null) _promptDataCache = storedData;

            // 2. Gyári promptok szinkronizálása az Assetekből a mappába (ha hiányoznak)
            await SynchronizeFactoryPromptsFromAssets();

            // 3. Az összes .txt fájl beolvasása a mappából
            var finalTemplates = new List<PromptTemplate>();
            var txtFiles = Directory.GetFiles(_promptsFolder, "*.txt");

            foreach (var filePath in txtFiles)
            {
                string title = Path.GetFileNameWithoutExtension(filePath);
                string content = await File.ReadAllTextAsync(filePath, Encoding.UTF8);

                finalTemplates.Add(new PromptTemplate
                {
                    Id = GenerateDeterministicGuid(title),
                    Title = SplitCamelCase(title),
                    Content = content
                });
            }

            _promptDataCache.Prompts = finalTemplates;

            // 4. Alapértelmezett aktív beállítása, ha nincs vagy érvénytelen
            if (_promptDataCache.Preferences.ActivePromptId == Guid.Empty ||
                !_promptDataCache.Prompts.Any(p => p.Id == _promptDataCache.Preferences.ActivePromptId))
            {
                var devPrompt = _promptDataCache.Prompts.FirstOrDefault(p => p.Title.Contains("Developer"))
                               ?? _promptDataCache.Prompts.FirstOrDefault();

                if (devPrompt != null)
                {
                    _promptDataCache.Preferences.ActivePromptId = devPrompt.Id;
                }
            }
        }

        private async Task SynchronizeFactoryPromptsFromAssets()
        {
            foreach (var name in _factoryPromptNames)
            {
                string targetPath = Path.Combine(_promptsFolder, $"{name}.txt");

                // Csak akkor másoljuk ki, ha még nem létezik a célmappában
                if (!File.Exists(targetPath))
                {
                    try
                    {
                        using var stream = await FileSystem.OpenAppPackageFileAsync($"Prompts/{name}.txt");
                        using var reader = new StreamReader(stream);
                        string content = await reader.ReadToEndAsync();
                        await File.WriteAllTextAsync(targetPath, content, Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Hiba a(z) {name} prompt asset betöltésekor: {ex.Message}");
                    }
                }
            }
        }

        private Guid GenerateDeterministicGuid(string input)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input.ToLowerInvariant()));
                return new Guid(hash);
            }
        }

        private string SplitCamelCase(string input)
        {
            return System.Text.RegularExpressions.Regex.Replace(input, "([a-z])([A-Z])", "$1 $2");
        }

        public async Task<List<PromptTemplate>> GetPromptsAsync()
        {
            await EnsureLoadedAsync();
            return _promptDataCache.Prompts.OrderBy(p => p.Title).ToList();
        }

        public async Task<string> GetSystemPromptAsync()
        {
            await EnsureLoadedAsync();
            var activeId = _promptDataCache.Preferences.ActivePromptId;
            var activePrompt = _promptDataCache.Prompts.FirstOrDefault(p => p.Id == activeId);
            return activePrompt?.Content ?? string.Empty;
        }

        public async Task<Guid> GetActivePromptIdAsync()
        {
            await EnsureLoadedAsync();
            return _promptDataCache.Preferences.ActivePromptId;
        }

        public async Task SaveAllAsync(List<PromptTemplate> prompts)
        {
            // Minden promptot elmentünk a megfelelő .txt fájlba
            foreach (var prompt in prompts)
            {
                // Biztonságos fájlnév készítése a címből (szóközök eltávolítása)
                string fileName = prompt.Title.Replace(" ", "") + ".txt";
                string filePath = Path.Combine(_promptsFolder, fileName);
                await File.WriteAllTextAsync(filePath, prompt.Content, Encoding.UTF8);
            }

            // A törölt promptok fájljainak eltávolítása a lemezről
            var currentFileNames = prompts.Select(p => p.Title.Replace(" ", "") + ".txt").ToHashSet();
            var existingFiles = Directory.GetFiles(_promptsFolder, "*.txt");
            foreach (var file in existingFiles)
            {
                if (!currentFileNames.Contains(Path.GetFileName(file)))
                {
                    File.Delete(file);
                }
            }

            _promptDataCache.Prompts = prompts;
            await _storage.WriteToFileAsync(PromptFileName, _promptDataCache);
        }

        public async Task SetActivePromptIdAsync(Guid id)
        {
            await EnsureLoadedAsync();
            _promptDataCache.Preferences.ActivePromptId = id;
            await _storage.WriteToFileAsync(PromptFileName, _promptDataCache);
        }
    }
}