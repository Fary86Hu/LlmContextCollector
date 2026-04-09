using System.Collections.Generic;
using System.Threading.Tasks;

namespace LlmContextCollector.Services
{
    public interface IImageClipboardService
    {
        Task CopyImagesToClipboardAsync(IEnumerable<string> filePaths);
        Task<string?> GetImageFromClipboardAsync();
    }
}