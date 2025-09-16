using System.Text.Json;

namespace LlmContextCollector.Services
{
    /// <summary>
    /// Általános szolgáltatás JSON fájlok olvasására és írására a felhasználó home könyvtárában.
    /// </summary>
    public class JsonStorageService
    {
        private readonly string _storagePath;

        public JsonStorageService()
        {
            _storagePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private string GetFullPath(string fileName) => Path.Combine(_storagePath, fileName);

        public async Task<T?> ReadFromFileAsync<T>(string fileName) where T : class
        {
            var path = GetFullPath(fileName);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var json = await File.ReadAllTextAsync(path);
                return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading or deserializing {fileName}: {ex.Message}");
                return null;
            }
        }

        public async Task WriteToFileAsync<T>(string fileName, T data) where T : class
        {
            var path = GetFullPath(fileName);
            try
            {
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(path, json);
            }
            catch (Exception ex)
            {
                 Console.WriteLine($"Error serializing or writing to {fileName}: {ex.Message}");
            }
        }
    }
}