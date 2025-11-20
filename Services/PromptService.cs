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
                        "   - Kezdje a választ egy rövid szöveges összefoglalóval a változásokról.\n\n" +
                        "   FÁJL GENERÁLÁSI ÉS FORMÁZÁSI PROTOKOLL (IGEN MAGAS PRIORITÁS):\n\n" +
                        "   1. TISZTA FÁJL TARTALOM ELV:\n" +
                        "      - A code blockon belüli tartalomnak KARAKTERHELYESEN egyeznie kell azzal, amit a lemezre mentenék.\n" +
                        "      - A válaszod NEM egy chat üzenet a kódon belül, hanem maga a nyers fájl.\n\n" +
                        "   2. META-KOMMENTEK TILTÁSA:\n" +
                        "      - SZIGORÚAN TILOS a változtatásokat magyarázó kommentek beszúrása a kód sorai közé (pl. TILOS: `// Itt adtam hozzá a checkboxot`, `// Modified line`).\n" +
                        "      - A magyarázatok helye KIZÁRÓLAG a [CHANGE_LOG] blokk.\n" +
                        "      - A kódban csak a nyelvhez szükséges szintaktikai elemek és eredeti dokumentációs kommentek maradhatnak.\n\n" +
                        "   3. TELJESSÉG ELVE (NO PLACEHOLDERS):\n" +
                        "      - A kódnak fordíthatónak és futtathatónak kell lennie a dobozból kivéve.\n" +
                        "      - SZIGORÚAN TILOS a '// ... a többi rész változatlan ...' vagy hasonló placeholder kommentek használata, mivel a válaszod közvetlenül felülírja a fájlt a lemezen! Minden sort ki kell írni.\n\n" +
                        "   - Minden fájl módosítást kötelezően az alábbi formátumban adjon meg:\n" +
                        "     [CHANGE_LOG]\n" +
                        "     Mit és miért változtatott...\n" +
                        "     [/CHANGE_LOG]\n" +
                        "     Fájl: Mappa/FajlNeve.cs\n" +
                        "     (Vagy új fájl esetén: Új Fájl: Mappa/UjNeve.cs)\n" +
                        "     ```kiterjesztés\n" +
                        "     // A FÁJL TELJES TARTALMA (PLACEHOLDEREK ÉS META-KOMMENTEK NÉLKÜL)\n" +
                        "     ```\n" +
                        "  -Ha vettél fel új lokalizációkat, akkor azokat mindig írd le a válaszod össefoglaló részének végén, a fájl módosítások elé, a következő formában:\n\n" +
                        "<data name=\"XY\" xml:space=\"preserve\">\n" +
                        "    <value>XY</value>\n" +
                        "  </data>";
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