using System;
using System.Threading.Tasks;

namespace LlmContextCollector.Services
{
    public class BrowserService
    {
        public event Action<string>? OnOpenBrowser;
        public event Action? OnCloseBrowser;
        public event Func<Task<string>>? OnExtractContent;
        
        // Esemény, amit a ContextPanel figyel, hogy megkapja a kinyert választ
        public event Func<string, Task>? OnContentExtracted;

        public bool IsBrowserOpen { get; private set; }

        public void OpenBrowser(string url)
        {
            IsBrowserOpen = true;
            OnOpenBrowser?.Invoke(url);
        }

        public void CloseBrowser()
        {
            IsBrowserOpen = false;
            OnCloseBrowser?.Invoke();
        }

        public async Task TriggerExtractionAsync()
        {
            if (OnExtractContent != null)
            {
                var content = await OnExtractContent.Invoke();
                if (OnContentExtracted != null)
                {
                    await OnContentExtracted.Invoke(content);
                }
            }
        }
    }
}