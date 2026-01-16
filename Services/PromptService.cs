using LlmContextCollector.Models;

namespace LlmContextCollector.Services
{
    public class PromptService
    {
        private const string PromptFileName = ".llm_context_prompts.json";
        private readonly JsonStorageService _storage;

        private PromptData _promptDataCache = new();

        private const string DefaultSystemPromptContent =
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
            "      - SZIGORÚAN TILOS a változtatásokat magyarázó kommentek beszúrása a kód sorai közé, vagy a függvények elé (pl. TILOS: `// Itt adtam hozzá a checkboxot`, `// Modified line`).\n" +
            "      - A magyarázatok helye KIZÁRÓLAG a [CHANGE_LOG] blokk a code rész előtt.\n" +
            "      - A kódban csak a nyelvhez szükséges szintaktikai elemek.\n\n" +
            "   3. TELJESSÉG ELVE (NO PLACEHOLDERS):\n" +
            "      - A kódnak fordíthatónak és futtathatónak kell lennie a dobozból kivéve.\n" +
            "      - SZIGORÚAN TILOS a '// ... a többi rész változatlan ...' vagy hasonló placeholder kommentek használata, ha a teljes fájlt írod ki.\n\n" +
            "   4. RÉSZLEGES MÓDOSÍTÁSOK (SEARCH/REPLACE BLOKKOK):\n" +
            "      - Ha egy fájl nagy és csak egy részét módosítod, NE írd ki az egészet. Használd a SEARCH/REPLACE formátumot a code blockon belül.\n" +
            "      - Formátum:\n" +
            "        <<<<<<< SEARCH\n" +
            "        (Eredeti kód részlet, PONTOSAN karakterre egyeznie kell a fájlban lévővel)\n" +
            "        =======\n" +
            "        (Új kód részlet, amire cserélni kell)\n" +
            "        >>>>>>> REPLACE\n" +
            "      - Egy fájlon belül több ilyen blokk is lehet.\n" +
            "      - A SEARCH blokkban lévő kódnak elegendő kontextust kell tartalmaznia, hogy egyedi legyen.\n\n" +
            "   - Minden fájl módosítást kötelezően az alábbi formátumban adjon meg:\n" +
            "     [CHANGE_LOG]\n" +
            "     Mit és miért változtatott... Itt a kommentek helye, ha le akarsz írni valami magyarázatot, akkor ide tedd meg\n" +
            "     [/CHANGE_LOG]\n" +
            "     Fájl: {Mappa1/Mappa2..}/{FajlNeve}.{kiterjesztés}\n" +
            "     ```\n" +
            "     (A FÁJL TELJES TARTALMA VAGY SEARCH/REPLACE BLOKKOK META-KOMMENTEK NÉLKÜL)\n" +
            "     ```\n" +
            "  -Ha vettél fel új lokalizációkat, akkor azokat mindig írd le a válaszod össefoglaló részének végén, a fájl módosítások elé, a következő formában:\n\n" +
            "  <data name=\"XY\" xml:space=\"preserve\">\n" +
            "    <value>XY</value>\n" +
            "  </data>";

        public PromptService(JsonStorageService storage)
        {
            _storage = storage;
        }

        private async Task EnsureLoadedAsync()
        {
            if (!_promptDataCache.Prompts.Any())
            {
                _promptDataCache = await _storage.ReadFromFileAsync<PromptData>(PromptFileName) ?? new PromptData();

                // Ha üres a lista, hozzuk létre a Default promptot
                if (!_promptDataCache.Prompts.Any())
                {
                    var defaultPrompt = new PromptTemplate
                    {
                        Id = Guid.NewGuid(),
                        Title = "Default",
                        Content = DefaultSystemPromptContent
                    };
                    _promptDataCache.Prompts.Add(defaultPrompt);
                    _promptDataCache.Preferences.ActivePromptId = defaultPrompt.Id;

                    // Mentés, hogy legközelebb meglegyen
                    await _storage.WriteToFileAsync(PromptFileName, _promptDataCache);
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
            _promptDataCache.Prompts = prompts;
            // Ha a jelenlegi aktív ID már nincs a listában (pl törölték), állítsuk vissza az elsőre vagy Defaultra
            if (!_promptDataCache.Prompts.Any(p => p.Id == _promptDataCache.Preferences.ActivePromptId))
            {
                _promptDataCache.Preferences.ActivePromptId = _promptDataCache.Prompts.FirstOrDefault()?.Id ?? Guid.Empty;
            }
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