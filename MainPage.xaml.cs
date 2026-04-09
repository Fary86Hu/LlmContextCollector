using LlmContextCollector.Models;
using LlmContextCollector.Services;

namespace LlmContextCollector
{
    public partial class MainPage : ContentPage
    {
        private AppState? _appState;
        private double _startFlex = 0.7;

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
                _appState = services.GetService<AppState>();
            }
        }

        private void OnTabClicked(object sender, EventArgs e)
        {
            var btn = (Button)sender;
            if (btn.CommandParameter == null) return;
            
            var target = btn.CommandParameter.ToString();

            TabChatBtn.TextColor = target == "Chat" ? Color.FromArgb("#0090ff") : Color.FromArgb("#9e9e9e");
            TabStudioBtn.TextColor = target == "Studio" ? Color.FromArgb("#0090ff") : Color.FromArgb("#9e9e9e");

            AiBlazorView.IsVisible = (target == "Chat" || target == "Logs");
            AiStudioWebView.IsVisible = (target == "Studio");

            if (AiBlazorView.IsVisible && _appState != null)
            {
                _appState.ActiveTab = target == "Chat" ? WorkbenchTab.Chat : WorkbenchTab.AgentLog;
            }
        }

        private void OnSplitterPanUpdated(object sender, PanUpdatedEventArgs e)
        {
            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    _startFlex = BlazorColumn.Width.Value;
                    break;
                case GestureStatus.Running:
                    var totalWidth = MainRootGrid.Width;
                    if (totalWidth <= 0) return;

                    var newWidth = (_startFlex * totalWidth) + e.TotalX;
                    var newFlex = Math.Clamp(newWidth / totalWidth, 0.1, 0.9);
                    
                    BlazorColumn.Width = new GridLength(newFlex, GridUnitType.Star);
                    AiColumn.Width = new GridLength(1 - newFlex, GridUnitType.Star);
                    break;
            }
        }
    }
}