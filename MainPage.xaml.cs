using LlmContextCollector.Services;
using System.Text.Json;

namespace LlmContextCollector
{
    public partial class MainPage : ContentPage
    {
        private BrowserService? _browserService;
        private string? _lastLoadedUrl;

        public MainPage()
        {
            InitializeComponent();
            Loaded += MainPage_Loaded;
        }

        private void MainPage_Loaded(object? sender, EventArgs e)
        {
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
                if (_lastLoadedUrl != url)
                {
                    InternalBrowser.Source = url;
                    _lastLoadedUrl = url;
                }
                BrowserOverlay.IsVisible = true;
            });
        }

        private void BrowserService_OnCloseBrowser()
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                BrowserOverlay.IsVisible = false;
                // InternalBrowser.Source = "about:blank"; // Kikommentelve a memória megőrzése érdekében
            });
        }

        private async Task<string> BrowserService_OnExtractContent()
        {
            try
            {
                string script = @"
            (function() {
                try {
                    var all = [];
                    function w(r) {
                        var n = r.querySelectorAll('*');
                        for (var i = 0; i < n.length; i++) {
                            var e = n[i];
                            if (e.tagName && e.tagName.toLowerCase() === 'ms-autosize-textarea') { all.push(e); }
                            if (e.shadowRoot) { w(e.shadowRoot); }
                        }
                    }
                    w(document);
                    
                    if (all.length === 0) return JSON.stringify({ status: 'NOT_FOUND', count: 0 });
                    
                    var last = all[all.length - 1];
                    var txt = last.getAttribute('data-value');
                    
                    if (!txt && last.querySelector('textarea')) {
                        txt = last.querySelector('textarea').value;
                    }
                    
                    return JSON.stringify({ status: 'OK', count: all.length, content: txt });
                } catch (err) {
                    return JSON.stringify({ status: 'ERROR', msg: err.toString() });
                }
            })();
        ";

                var result = await InternalBrowser.EvaluateJavaScriptAsync(script);

                if (result == null) return "KRITIKUS HIBA: A JS kód szintaktikai hiba miatt nem futott le.";

                try
                {
                    var jsonString = System.Text.Json.JsonSerializer.Deserialize<string>(result);

                    if (jsonString == null) return "Üres JSON válasz.";

                    using (var doc = System.Text.Json.JsonDocument.Parse(jsonString))
                    {
                        var root = doc.RootElement;
                        var status = root.GetProperty("status").GetString();

                        if (status == "OK")
                        {
                            var content = root.GetProperty("content").GetString();
                            return content ?? "Az elem megvan, de a tartalom üres.";
                        }
                        else if (status == "ERROR")
                        {
                            return $"JS HIBA: {root.GetProperty("msg").GetString()}";
                        }
                        else
                        {
                            var count = root.GetProperty("count").GetInt32();
                            return $"NEM TALÁLHATÓ. (Találatok száma: {count}). Ellenőrizd, hogy betöltött-e az oldal.";
                        }
                    }
                }
                catch (Exception parseEx)
                {
                    return $"JSON PARSE HIBA: {parseEx.Message} | Nyers: {result}";
                }
            }
            catch (Exception ex)
            {
                return $"C# KIVÉTEL: {ex.Message}";
            }
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