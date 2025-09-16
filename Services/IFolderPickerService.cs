namespace LlmContextCollector.Services
{
    public interface IFolderPickerService
    {
        Task<string?> PickFolderAsync();
    }
}