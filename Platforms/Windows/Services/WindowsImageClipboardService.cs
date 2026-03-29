using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LlmContextCollector.Services;
using Microsoft.Maui.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LlmContextCollector.WinUI.Services
{
    public class WindowsImageClipboardService : IImageClipboardService
    {
        public async Task CopyImagesToClipboardAsync(IEnumerable<string> filePaths)
        {
            var validPaths = filePaths.Where(File.Exists).ToList();
            if (!validPaths.Any()) return;

            var storageFiles = new List<IStorageItem>();
            foreach (var path in validPaths)
            {
                storageFiles.Add(await StorageFile.GetFileFromPathAsync(path));
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dataPackage.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                    dataPackage.SetStorageItems(storageFiles);

                    var firstFile = (StorageFile)storageFiles.FirstOrDefault();
                    if (firstFile != null)
                    {
                        var streamRef = RandomAccessStreamReference.CreateFromFile(firstFile);
                        dataPackage.SetBitmap(streamRef);
                    }

                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to set clipboard: {ex.Message}");
                }
            });
        }
    }
}