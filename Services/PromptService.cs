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
            if (!_promptDataCache.Prompts.Any() && string.IsNullOrEmpty(_promptDataCache.Preferences.GlobalPrefix) && string.IsNullOrEmpty(_promptDataCache.Preferences.DeveloperPrompt))
            {
                 _promptDataCache = await _storage.ReadFromFileAsync<PromptData>(PromptFileName) ?? new PromptData();
                 
                 // 1. GLOBAL PREFIX: Az Implementáló (Code Generator) Prompt
                 // Ez felelős azért, hogy a válasz formátuma feldolgozható legyen a DiffDialog számára.
                 if (string.IsNullOrEmpty(_promptDataCache.Preferences.GlobalPrefix))
                 {
                     _promptDataCache.Preferences.GlobalPrefix = 
                         "Szenior szoftverfejlesztőként a feladatod a kért kódmódosítások elvégzése.\n" +
                         "A válaszodat egy gép dolgozza fel, ezért SZIGORÚAN tartsd be az alábbi formátumot:\n\n" +
                         "1. FÁJLOK DEFINIÁLÁSA:\n" +
                         "Minden módosított fájlt egy [CHANGE_LOG] blokk vezessen be, majd a fájl fejléce, végül a kódblokk.\n" +
                         "Formátum:\n" +
                         "[CHANGE_LOG]\n" +
                         "Rövid leírás, hogy mit és miért változtattál ebben a fájlban.\n" +
                         "[/CHANGE_LOG]\n" +
                         "Meglévő fájl: Fájl: Mappa/FajlNeve.cs\n" +
                         "Új fájl: Új Fájl: Mappa/UjFajlNeve.cs\n" +
                         "```csharp\n" +
                         "// ... TELJES KÓDTARTALOM ...\n" +
                         "```\n\n" +
                         "2. KÓD TARTALMA:\n" +
                         "MINDIG a fájl TELJES, MŰKÖDŐ tartalmát add vissza. TILOS részleges kódot vagy kommenteket (pl. '// ... a többi rész változatlan ...') használni, mert a válaszod felülírja a fájlt.\n\n" +
                         "3. GLOBÁLIS ÖSSZEFOGLALÓ:\n" +
                         "A válasz legelején (a fájlok előtt) adj egy rövid felsorolást a változtatásokról.";
                 }

                 // 2. DEVELOPER PROMPT: Az Architect (Analyst) Prompt
                 // Ez felelős a feladat tisztázásáért. A [Qx] tageket a ClarificationDialog dolgozza fel.
                 if (string.IsNullOrEmpty(_promptDataCache.Preferences.DeveloperPrompt))
                 {
                     _promptDataCache.Preferences.DeveloperPrompt =
                         "Ön egy tapasztalt szoftver architect. Az Ön elsődleges feladata, hogy a fejlesztési kéréseket aprólékosan átvizsgálja, mielőtt bármilyen implementáció megkezdődne. Elemeznie kell a megadott kontextust, amely egy felhasználói promptból, egy globális rendszer promptból és több kódfájlból áll.\n\n" +
                         "A fő célja NEM a kód megírása vagy módosítása. Ehelyett az Ön feladata, hogy azonosítson minden kétértelműséget, hiányzó információt vagy tisztázatlan követelményt.\n\n" +
                         "ELEMZÉSI SZEMPONTOK:\n" +
                         "- Specifikusság: Egyértelműek a UI elemek és interakciók?\n" +
                         "- Teljesség: Minden szükséges fájl megvan a kontextusban?\n" +
                         "- Konzisztencia: Vannak logikai ellentmondások?\n" +
                         "- Szélsőséges esetek: Kezelve vannak a hibák és edge case-ek?\n" +
                         "- Függőségek: Vannak nem említett mellékhatások?\n\n" +
                         "KIMENETI FORMÁTUM (KÖTELEZŐ):\n" +
                         "1. ÖSSZEFOGLALÓ: Röviden foglalja össze, hogyan értelmezte a célt.\n\n" +
                         "2. TISZTÁZANDÓ KÉRDÉSEK:\n" +
                         "Minden kérdést külön, sorszámozott blokkba kell tenni, hogy a rendszer fel tudja dolgozni őket. A formátum: [Q1]Kérdés szövege...[/Q1], [Q2]Kérdés...[/Q2].\n" +
                         "Példa:\n" +
                         "[Q1]\n" +
                         "Pontosan milyen típusú adatbázist szeretne használni ehhez a funkcióhoz?\n" +
                         "[/Q1]\n" +
                         "[Q2]\n" +
                         "A 'Mentés' gomb lenyomásakor kell validációt futtatni a kliens oldalon is?\n" +
                         "[/Q2]\n\n" +
                         "3. HIÁNYZÓ KONTEXTUS:\n" +
                         "Ha hiányoznak fájlok, listázza őket egyetlen blokkban:\n" +
                         "[MISSING_CONTEXT]UserService.cs, IRepository.cs[/MISSING_CONTEXT]\n" +
                         "Ha nincs hiányzó fájl, hagyja üresen ezt a blokkot.\n\n" +
                         "DÖNTŐ SZABÁLY: NE generáljon semmilyen programkódot (C#, JS, stb.). A válasz kizárólag elemzés és kérdések listája lehet.";
                 }
            }
        }

        public async Task<List<PromptTemplate>> GetPromptsAsync()
        {
            await EnsureLoadedAsync();
            return _promptDataCache.Prompts.OrderBy(p => p.Title).ToList();
        }

        public async Task<string> GetGlobalPrefixAsync()
        {
            await EnsureLoadedAsync();
            return _promptDataCache.Preferences.GlobalPrefix;
        }
        
        public async Task<string> GetDeveloperPromptAsync()
        {
            await EnsureLoadedAsync();
            return _promptDataCache.Preferences.DeveloperPrompt;
        }

        public async Task SaveAllAsync(List<PromptTemplate> prompts, string globalPrefix, string developerPrompt)
        {
            _promptDataCache.Prompts = prompts;
            _promptDataCache.Preferences.GlobalPrefix = globalPrefix;
            _promptDataCache.Preferences.DeveloperPrompt = developerPrompt;

            await _storage.WriteToFileAsync(PromptFileName, _promptDataCache);
        }
    }
}