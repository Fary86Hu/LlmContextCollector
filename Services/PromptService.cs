using LlmContextCollector.Models;

namespace LlmContextCollector.Services
{
    public class PromptService
    {
        private const string PromptFileName = ".llm_context_prompts.json";
        private readonly JsonStorageService _storage;
        
        private PromptData _promptDataCache = new();

        public PromptService(JsonStorageService storage)
        {
            _storage = storage;
        }
        
        private async Task EnsureLoadedAsync()
        {
            if (!_promptDataCache.Prompts.Any() && string.IsNullOrEmpty(_promptDataCache.Preferences.SystemPrompt))
            {
                 _promptDataCache = await _storage.ReadFromFileAsync<PromptData>(PromptFileName) ?? new PromptData();
                 
                 // UNIFIED SYSTEM PROMPT
                 // Ez a prompt kezeli mind a tisztázó kérdéseket, mind az implementációt.
                 if (string.IsNullOrEmpty(_promptDataCache.Preferences.SystemPrompt))
                 {
                     _promptDataCache.Preferences.SystemPrompt =
                         "Ön egy tapasztalt Szenior Szoftverfejlesztő és Architect.\n\n" +
                         "FELADAT:\n" +
                         "Elemezze a megadott kontextust (fájlok) és a felhasználói kérést.\n\n" +
                         "DÖNTÉSI SZABÁLY (KIZÁRÓ VAGY):\n" +
                         "A kérés természete alapján válasszon az alábbi két kimeneti mód közül (TILOS keverni őket):\n\n" +
                         "1. ÚTVONAL: TISZTÁZÁS (Ha a kérés kétértelmű, hiányos, vagy kockázatos feltételezéseket igényel)\n" +
                         "   - NE generáljon kódot.\n" +
                         "   - Tegyen fel tisztázó kérdéseket [Qx] formátumban.\n" +
                         "   - Ha fájlok hiányoznak a kontextusból, listázza őket [MISSING_CONTEXT] blokkban.\n" +
                         "   - Formátum:\n" +
                         "     [Q1]Kérdés szövege...[/Q1]\n" +
                         "     [MISSING_CONTEXT]UserService.cs, IRepository.cs[/MISSING_CONTEXT]\n\n" +
                         "2. ÚTVONAL: IMPLEMENTÁCIÓ (Ha a kérés egyértelmű és végrehajtható)\n" +
                         "   - Generálja le a szükséges kódváltoztatásokat.\n" +
                         "   - NE tegyen fel kérdéseket.\n" +
                         "   - Kezdje a választ egy rövid szöveges összefoglalóval a változásokról.\n" +
                         "   - KRITIKUS SZABÁLY: Minden fájl esetén a TELJES, MŰKÖDŐ kódot kell visszaadnia. SZIGORÚAN TILOS a '// ... a többi rész változatlan ...' vagy hasonló placeholder kommentek használata, mivel a válaszod közvetlenül felülírja a fájlt a lemezen!\n" +
                         "   - Minden fájl módosítást kötelezően az alábbi formátumban adjon meg:\n" +
                         "     [CHANGE_LOG]\n" +
                         "     Mit és miért változtatott...\n" +
                         "     [/CHANGE_LOG]\n" +
                         "     Fájl: Mappa/FajlNeve.cs\n" +
                         "     (Vagy új fájl esetén: Új Fájl: Mappa/UjNeve.cs)\n" +
                         "     ```kiterjesztés\n" +
                         "     // A FÁJL TELJES TARTALMA (PLACEHOLDEREK NÉLKÜL)\n" +
                         "     ```" +
                         "Ha vettél fel új lokalizációkat, akkor azokat mindig írd le a válaszod össefoglaló részének végén, a fájl módosítások elé, a következõ formában:\r\n\r\n  <data name=\"XY\" xml:space=\"preserve\">\r\n    <value>XY</value>\r\n  </data>";
                 }
            }
        }

        public async Task<List<PromptTemplate>> GetPromptsAsync()
        {
            await EnsureLoadedAsync();
            return _promptDataCache.Prompts.OrderBy(p => p.Title).ToList();
        }

        public async Task<string> GetSystemPromptAsync()
        {
            await EnsureLoadedAsync();
            return _promptDataCache.Preferences.SystemPrompt;
        }
        
        public async Task SaveAllAsync(List<PromptTemplate> prompts, string systemPrompt)
        {
            _promptDataCache.Prompts = prompts;
            _promptDataCache.Preferences.SystemPrompt = systemPrompt;

            await _storage.WriteToFileAsync(PromptFileName, _promptDataCache);
        }
    }
}