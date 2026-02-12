using LlmContextCollector.Models;
using LlmContextCollector.Services;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using System.Text.Json;

namespace LlmContextCollector
{
    public partial class MainPage : ContentPage
    {
        private BrowserService? _browserService;
        private AppState? _appState;
        private ContextProcessingService? _contextProcessingService;
        private IClipboard? _clipboard;

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
                _appState = services.GetService<AppState>();
                _contextProcessingService = services.GetService<ContextProcessingService>();
                _clipboard = services.GetService<IClipboard>();

                if (_browserService != null)
                {
                    _browserService.OnOpenBrowser += BrowserService_OnOpenBrowser;
                    _browserService.OnCloseBrowser += BrowserService_OnCloseBrowser;
                    _browserService.OnExtractContent += BrowserService_OnExtractContent;
                }

                if (_appState != null)
                {
                    _appState.PropertyChanged += AppState_PropertyChanged;
                    UpdatePromptPicker();
                }
            }
        }

        private void AppState_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppState.PromptTemplates) || e.PropertyName == nameof(AppState.ActiveGlobalPromptId))
            {
                MainThread.BeginInvokeOnMainThread(UpdatePromptPicker);
            }
        }

        private void UpdatePromptPicker()
        {
            if (_appState == null || _appState.PromptTemplates == null) return;

            var templates = _appState.PromptTemplates.ToList();
            if (templates.Count == 0) return;

            // Először leállítjuk az eseménykezelőt, hogy a betöltés ne váltson ki felesleges mentést
            SystemPromptPicker.SelectedIndexChanged -= SystemPromptPicker_SelectedIndexChanged;

            SystemPromptPicker.ItemsSource = templates;
            
            var selected = templates.FirstOrDefault(p => p.Id == _appState.ActiveGlobalPromptId) 
                           ?? templates.FirstOrDefault();

            if (selected != null)
            {
                SystemPromptPicker.SelectedItem = selected;
            }

            SystemPromptPicker.SelectedIndexChanged += SystemPromptPicker_SelectedIndexChanged;
        }

        private void SystemPromptPicker_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (SystemPromptPicker.SelectedItem is PromptTemplate selected && _appState != null)
            {
                if (_appState.ActiveGlobalPromptId != selected.Id)
                {
                    _appState.ActiveGlobalPromptId = selected.Id;
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
                
                if (_appState != null)
                {
                    PromptEditor.Text = _appState.PromptText;
                }

                BrowserOverlay.IsVisible = true;
            });
        }

        private void BrowserService_OnCloseBrowser()
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                BrowserOverlay.IsVisible = false;
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

        private async void CopyContext_Clicked(object sender, EventArgs e)
        {
            if (_appState == null || _contextProcessingService == null || _clipboard == null)
            {
                return;
            }

            _appState.PromptText = PromptEditor.Text ?? string.Empty;

            try
            {
                var sortedFiles = _appState.SelectedFilesForContext.OrderBy(x => x).ToList();
                var content = await _contextProcessingService.BuildContextForClipboardAsync(
                    includePrompt: PromptCheckBox.IsChecked,
                    includeSystemPrompt: SystemPromptCheckBox.IsChecked,
                    includeFiles: FileContextCheckBox.IsChecked,
                    sortedFilePaths: sortedFiles);

                if (string.IsNullOrWhiteSpace(content))
                {
                    return;
                }

                await _clipboard.SetTextAsync(content);
                
                var originalText = CopyButton.Text;
                CopyButton.Text = "Másolva! ✓";
                CopyButton.BackgroundColor = Microsoft.Maui.Graphics.Colors.Green;
                
                await Task.Delay(2000);
                
                CopyButton.Text = originalText;
                CopyButton.BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#0078d4");
            }
            catch (Exception)
            {
                CopyButton.Text = "Hiba!";
                CopyButton.BackgroundColor = Microsoft.Maui.Graphics.Colors.Red;
                await Task.Delay(2000);
                CopyButton.Text = "Másolás";
                CopyButton.BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#0078d4");
            }
        }

        private void PromptLabel_Tapped(object sender, EventArgs e)
        {
            PromptCheckBox.IsChecked = !PromptCheckBox.IsChecked;
        }

        private void SystemLabel_Tapped(object sender, EventArgs e)
        {
            SystemPromptCheckBox.IsChecked = !SystemPromptCheckBox.IsChecked;
        }

        private void FilesLabel_Tapped(object sender, EventArgs e)
        {
            FileContextCheckBox.IsChecked = !FileContextCheckBox.IsChecked;
        }
    }
}