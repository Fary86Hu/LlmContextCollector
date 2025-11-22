using LlmContextCollector.Services;

namespace LlmContextCollector
{
    public partial class MainPage : ContentPage
    {
        private BrowserService? _browserService;

        public MainPage()
        {
            InitializeComponent();
            
            // Feliratkozás az eseményekre, amint a Handler létrejön és a Services elérhető
            Loaded += MainPage_Loaded;
        }

        private void MainPage_Loaded(object? sender, EventArgs e)
        {
            // Service feloldása
            var services = this.Handler?.MauiContext?.Services;
            if (services != null)
            {
                _browserService = services.GetService<BrowserService>();
                if (_browserService != null)
                {
                    _browserService.OnOpenBrowser += BrowserService_OnOpenBrowser;
                    _browserService.OnCloseBrowser += BrowserService_OnCloseBrowser;
                    _browserService.OnExtractContent += BrowserService_OnExtractContent;
                }
            }
        }

        private void BrowserService_OnOpenBrowser(string url)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                InternalBrowser.Source = url;
                BrowserOverlay.IsVisible = true;
            });
        }

        private void BrowserService_OnCloseBrowser()
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                BrowserOverlay.IsVisible = false;
                InternalBrowser.Source = "about:blank"; // Tisztítás
            });
        }

        private Task<string> BrowserService_OnExtractContent()
        {
            // Mivel a felhasználó kérése alapján a vágólapról dolgozunk,
            // itt nem nyerünk ki adatot a WebView-ból, csak jelezzük a folyamat végét.
            return Task.FromResult(string.Empty);
        }

        private void CloseBrowser_Clicked(object sender, EventArgs e)
        {
            _browserService?.CloseBrowser();
        }

        private async void ExtractBrowser_Clicked(object sender, EventArgs e)
        {
            if (_browserService != null)
            {
                await _browserService.TriggerExtractionAsync();
            }
        }
    }
}