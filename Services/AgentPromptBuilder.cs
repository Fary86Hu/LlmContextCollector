using System.Text;
using LlmContextCollector.Models;

namespace LlmContextCollector.Services
{
    public class AgentPromptBuilder
    {
        public string BuildPrompt(AgentSearchSession session, string newlyLoadedContent)
        {
            var sb = new StringBuilder();

            sb.AppendLine("Ön egy Szoftver Architect Kereső Ágens. A feladata, hogy egy ismeretlen projekt struktúrájából iteratívan kiválassza azokat a fájlokat, amelyek szükségesek a felhasználói feladat megértéséhez és megoldásához.");
            sb.AppendLine();
            sb.AppendLine("SZABÁLYOK:");
            sb.AppendLine("1. Csak a legszükségesebb fájlokat kérje be.");
            sb.AppendLine("2. Válaszában CSAK a fájlok relatív elérési útját sorolja fel, minden fájlt új sorba.");
            sb.AppendLine("3. Ha úgy ítéli meg, hogy a rendelkezésre álló információ elegendő a feladat végrehajtásához (implementációhoz), írja le a 'READY' szót egyedül.");
            sb.AppendLine("4. NE írjon magyarázatot, csak a listát vagy a READY szót.");
            sb.AppendLine();

            sb.AppendLine($"--- FELHASZNÁLÓI FELADAT ---\n{session.UserTask}\n");

            if (session.CurrentRound == 1)
            {
                sb.AppendLine("--- PROJEKT STRUKTÚRA ---");
                sb.AppendLine(session.ProjectStructure);
                sb.AppendLine();
                sb.AppendLine("INSTRUKCIÓ: A fenti struktúra és a feladat alapján válasszon ki 5-10 fájlt kezdésnek, amelyek a legrelevánsabbnak tűnnek (pl. belépési pontok, fő service-ek, config).");
            }
            else
            {
                sb.AppendLine("--- EDDIG ELEMZETT FÁJLOK (Már ismeri a tartalmukat) ---");
                if (session.FilesSeenAsNames.Count > 0)
                {
                    foreach (var file in session.FilesSeenAsNames)
                    {
                        sb.AppendLine($"- {file}");
                    }
                }
                else
                {
                    sb.AppendLine("(Nincs)");
                }
                sb.AppendLine();

                sb.AppendLine("--- ÚJONNAN BEKÉRT FÁJLOK TARTALMA (Most olvassa el) ---");
                if (!string.IsNullOrWhiteSpace(newlyLoadedContent))
                {
                    sb.AppendLine(newlyLoadedContent);
                }
                else
                {
                    sb.AppendLine("(Nincs új tartalom)");
                }
                sb.AppendLine();

                sb.AppendLine("INSTRUKCIÓ: A fenti új információk alapján folytassa az elemzést.");
                sb.AppendLine("- Ha további fájlokra van szüksége a megértéshez (pl. lát egy hivatkozást egy ismeretlen osztályra), listázza azokat.");
                sb.AppendLine("- Ha minden szükséges infó megvan a kódoláshoz, válaszolja: READY");
            }

            return sb.ToString();
        }
    }
}