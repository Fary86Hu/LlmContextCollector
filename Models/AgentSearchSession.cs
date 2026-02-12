using System.Collections.Generic;

namespace LlmContextCollector.Models
{
    public class AgentSearchSession
    {
        public int CurrentRound { get; set; } = 1;
        public string ProjectStructure { get; set; } = string.Empty;
        public string UserTask { get; set; } = string.Empty;
        
        /// <summary>
        /// Azon fájlok, amelyeket az ágens korábbi körökben már megkapott és feldolgozott.
        /// Ezeknek a tartalmát már nem küldjük el újra, csak a nevüket a "Context" listában.
        /// </summary>
        public HashSet<string> FilesSeenAsNames { get; set; } = new();

        /// <summary>
        /// Azon fájlok, amelyeket az ágens a legutóbbi válaszában kért.
        /// Ezeknek a teljes tartalmát be kell tölteni a következő körhöz.
        /// </summary>
        public HashSet<string> FilesToLoadFullContent { get; set; } = new();
    }
}