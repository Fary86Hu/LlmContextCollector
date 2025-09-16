using LlmContextCollector.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace LlmContextCollector.WinUI.Services
{
    public class FolderPickerService : IFolderPickerService
    {
        public async Task<string?> PickFolderAsync()
        {
            var folderPicker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop,
                FileTypeFilter = { "*" }
            };

            // A WinUI ablak handle-jének megszerzése
            var mauiWindow = App.Current?.Application.Windows.FirstOrDefault();
            if (mauiWindow == null) return null;

            var nativeWindow = mauiWindow.Handler?.PlatformView;
            if (nativeWindow == null) return null;

            var hwnd = WindowNative.GetWindowHandle(nativeWindow);
            InitializeWithWindow.Initialize(folderPicker, hwnd);

            var result = await folderPicker.PickSingleFolderAsync();

            return result?.Path;
        }
    }
}