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

        public async Task<string?> GetImageFromClipboardAsync()
        {
            return await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                try
                {
                    var dataPackageView = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                    
                    if (dataPackageView.Contains(StandardDataFormats.Bitmap))
                    {
                        var streamRef = await dataPackageView.GetBitmapAsync();
                        using var stream = await streamRef.OpenReadAsync();
                        using var memoryStream = new MemoryStream();
                        await stream.AsStreamForRead().CopyToAsync(memoryStream);
                        var bytes = memoryStream.ToArray();
                        return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
                    }

                    if (dataPackageView.Contains(StandardDataFormats.StorageItems))
                    {
                        var items = await dataPackageView.GetStorageItemsAsync();
                        if (items.Count > 0 && items[0] is StorageFile storageFile)
                        {
                            var ext = storageFile.FileType.ToLower();
                            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp")
                            {
                                using var stream = await storageFile.OpenReadAsync();
                                using var memoryStream = new MemoryStream();
                                await stream.AsStreamForRead().CopyToAsync(memoryStream);
                                var mime = (ext == ".png") ? "image/png" : "image/jpeg";
                                return $"data:{mime};base64,{Convert.ToBase64String(memoryStream.ToArray())}";
                            }
                        }
                    }
                }
                catch { }
                return null;
            });
        }
    }
}