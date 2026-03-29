using LlmContextCollector.Models;
using Microsoft.AspNetCore.Components;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace LlmContextCollector.Services
{
    public class AzureDevOpsService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AppState _appState;
        private readonly AppLogService _logService;
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        public AzureDevOpsService(IHttpClientFactory httpClientFactory, AppState appState, AppLogService logService)
        {
            _httpClientFactory = httpClientFactory;
            _appState = appState;
            _logService = logService;
        }

        private string? GetSettingsPathForProject(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot)) return null;
            var projectFolderName = new DirectoryInfo(projectRoot).Name;
            var settingsDir = Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory, projectFolderName);
            Directory.CreateDirectory(settingsDir);
            return Path.Combine(settingsDir, "ado_settings.json");
        }

        public async Task LoadSettingsForCurrentProjectAsync()
        {
            var path = GetSettingsPathForProject(_appState.ProjectRoot);
            AdoProjectSettings? settings = null;
            if (path != null && File.Exists(path))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(path);
                    settings = JsonSerializer.Deserialize<AdoProjectSettings>(json);
                }
                catch { }
            }
            settings ??= new AdoProjectSettings();

            // A kapcsolódási adatokat már globálisan tároljuk, csak a projekt-specifikus infókat töltjük be innen
            _appState.AdoLastDownloadDate = settings.LastFullDownloadUtc;
            _appState.LocalizationResourcePath = settings.LocalizationResourcePath;
        }

        public async Task SaveSettingsForCurrentProjectAsync(DateTime? newDownloadTimestamp = null)
        {
            var path = GetSettingsPathForProject(_appState.ProjectRoot);
            if (path == null) return;

            var settings = new AdoProjectSettings
            {
                // A kapcsolódási adatok a globális settings-be kerültek, ide csak a státusz infók
                LastFullDownloadUtc = newDownloadTimestamp ?? _appState.AdoLastDownloadDate,
                LocalizationResourcePath = _appState.LocalizationResourcePath
            };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);

            if (newDownloadTimestamp.HasValue)
            {
                _appState.AdoLastDownloadDate = newDownloadTimestamp;
            }
        }

        public void UpdateAdoPaths(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
            {
                _appState.AdoDocsPath = string.Empty;
                _appState.AdoDocsExist = false;
                return;
            }

            var projectFolderName = new DirectoryInfo(projectRoot).Name;
            var newPath = Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory, projectFolderName, "ado");
            var newExist = Directory.Exists(newPath) && Directory.EnumerateFiles(newPath, "*.txt").Any();

            _appState.AdoDocsPath = newPath;
            _appState.AdoDocsExist = newExist;
        }

        public async Task<(string Text, List<AttachedImage> Images)> GetFormattedWorkItemAsync(int workItemId)
        {
            _logService.LogInfo("ADO", $"Work Item {workItemId} lekérése indítva...");

            if (string.IsNullOrWhiteSpace(_appState.AzureDevOpsOrganizationUrl) || string.IsNullOrWhiteSpace(_appState.AzureDevOpsPat))
            {
                _logService.LogError("ADO", "Hiányzó konfiguráció (URL vagy PAT).");
                throw new InvalidOperationException("Az Azure DevOps beállítások (URL, PAT) nincsenek megadva.");
            }

            var orgUrl = _appState.AzureDevOpsOrganizationUrl.Trim().TrimEnd('/');
            var project = _appState.AzureDevOpsProject.Trim();
            var pat = _appState.AzureDevOpsPat.Trim();
            var encodedProject = Uri.EscapeDataString(project);

            var client = _httpClientFactory.CreateClient("AzureDevOps");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

            var url = $"{orgUrl}/{encodedProject}/_apis/wit/workitems/{workItemId}?$expand=all&api-version=6.0";
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Nem sikerült lekérni a munkamintát ({workItemId}). Állapot: {response.StatusCode}");
            }

            var workItem = await JsonSerializer.DeserializeAsync<WorkItem>(await response.Content.ReadAsStreamAsync(), _jsonOptions);
            if (workItem == null) 
            {
                _logService.LogWarning("ADO", $"Work Item {workItemId} nem található vagy üres.");
                return (string.Empty, new List<AttachedImage>());
            }

            // 1. Kommentek lekérése előre, hogy lássuk az inline képeket is
            var commentsUrl = $"{orgUrl}/{encodedProject}/_apis/wit/workitems/{workItemId}/comments?api-version=6.0-preview.3";
            var commentsResponse = await client.GetAsync(commentsUrl);
            var commentsData = commentsResponse.IsSuccessStatusCode 
                ? await JsonSerializer.DeserializeAsync<WorkItemCommentListResponse>(await commentsResponse.Content.ReadAsStreamAsync(), _jsonOptions)
                : null;

            // 2. Képek összegyűjtése (relations + inline)
            var attachmentMap = new Dictionary<string, string>(); 
            var downloadTasks = new List<(string UrlKey, Task<AttachedImage?> Task)>();
            var processedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // A: Relations listából
            var relations = workItem.Relations?.Where(r => r.Rel == "AttachedFile").ToList() ?? new();
            foreach (var rel in relations)
            {
                string originalName = "";
                if (rel.Attributes != null && rel.Attributes.TryGetValue("name", out var nameObj) && nameObj is JsonElement nameElem)
                    originalName = nameElem.GetString() ?? "";

                if (string.IsNullOrWhiteSpace(originalName))
                {
                    var nameMatch = Regex.Match(rel.Url, @"fileName=([^&]+)");
                    originalName = nameMatch.Success ? HttpUtility.UrlDecode(nameMatch.Groups[1].Value) : "attachment";
                }

                var ext = Path.GetExtension(originalName).ToLowerInvariant();
                if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp" || string.IsNullOrEmpty(ext))
                {
                    if (processedUrls.Add(rel.Url))
                    {
                        var urlId = Regex.Match(rel.Url, @"/attachments/([^/?#]+)").Groups[1].Value;
                        if (string.IsNullOrEmpty(urlId)) urlId = Guid.NewGuid().ToString("N").Substring(0, 8);
                        
                        var uniqueFileName = $"{workItemId}_{urlId}_{originalName}";
                        if (string.IsNullOrEmpty(ext)) uniqueFileName += ".png";

                        _logService.LogInfo("ADO", $"Csatolmány kép észlelve: {originalName}");
                        downloadTasks.Add((rel.Url, DownloadAttachmentAsync(client, rel.Url, uniqueFileName, workItemId)));
                    }
                }
                else
                {
                    _logService.LogInfo("ADO", $"Csatolmány kihagyva (típusa alapján nem kép): {originalName}");
                }
            }

            // B: Inline képek keresése a szöveges mezőkben és kommentekben
            var allHtml = new StringBuilder();
            allHtml.Append(GetFieldAsString(workItem, "System.Description"));
            allHtml.Append(GetFieldAsString(workItem, "Microsoft.VSTS.TCM.ReproSteps"));
            allHtml.Append(GetFieldAsString(workItem, "Microsoft.VSTS.Common.AcceptanceCriteria"));
            if (commentsData?.Comments != null)
                foreach (var c in commentsData.Comments) allHtml.Append(c.Text);

            var inlineUrls = ExtractImageUrlsFromHtml(allHtml.ToString());
            foreach (var imurl in inlineUrls)
            {
                if (processedUrls.Add(imurl))
                {
                    var fileNameMatch = Regex.Match(imurl, @"fileName=([^&]+)");
                    var originalName = fileNameMatch.Success ? HttpUtility.UrlDecode(fileNameMatch.Groups[1].Value) : "inline_image.png";
                    var urlId = Regex.Match(imurl, @"/attachments/([^/?#]+)").Groups[1].Value;
                    if (string.IsNullOrEmpty(urlId)) urlId = Guid.NewGuid().ToString("N").Substring(0, 8);

                    var uniqueFileName = $"{workItemId}_{urlId}_{originalName}";
                    _logService.LogInfo("ADO", $"Inline kép észlelve a szövegben: {originalName}");
                    downloadTasks.Add((imurl, DownloadAttachmentAsync(client, imurl, uniqueFileName, workItemId)));
                }
            }

            // 3. Letöltések végrehajtása
            var downloadedImages = new List<AttachedImage>();
            if (downloadTasks.Any())
            {
                await Task.WhenAll(downloadTasks.Select(t => t.Task));
                foreach (var taskInfo in downloadTasks)
                {
                    var resultImg = await taskInfo.Task;
                    if (resultImg != null)
                    {
                        downloadedImages.Add(resultImg);
                        attachmentMap[taskInfo.UrlKey] = resultImg.FileName;
                        var guidMatch = Regex.Match(taskInfo.UrlKey, @"/attachments/([^/?#]+)");
                        if (guidMatch.Success) attachmentMap[guidMatch.Groups[1].Value] = resultImg.FileName;
                    }
                }
            }

            // 4. Kommentek formázása a már letöltött képekkel
            var commentsText = new StringBuilder();
            if (commentsData?.Comments != null && commentsData.Comments.Any())
            {
                commentsText.AppendLine("\nKözösségi megjegyzések / Kommentek:");
                foreach (var comment in commentsData.Comments.OrderByDescending(c => c.CreatedDate).Take(10))
                {
                    commentsText.AppendLine($"[{comment.CreatedDate:yyyy-MM-dd HH:mm}] {comment.CreatedBy?.DisplayName}:");
                    commentsText.AppendLine(ProcessHtmlWithImageMarkers(comment.Text, attachmentMap));
                    commentsText.AppendLine("---");
                }
            }

            var sb = new StringBuilder();
            var title = GetFieldAsString(workItem, "System.Title");
            sb.AppendLine($"# ADO Work Item {workItemId}: {title}");
            sb.AppendLine($"Típus: {GetFieldAsString(workItem, "System.WorkItemType")}");
            sb.AppendLine($"Állapot: {GetFieldAsString(workItem, "System.State")}");
            
            sb.AppendLine("\nLeírás:");
            sb.AppendLine(ProcessHtmlWithImageMarkers(GetFieldAsString(workItem, "System.Description"), attachmentMap));
            
            var reproSteps = GetFieldAsString(workItem, "Microsoft.VSTS.TCM.ReproSteps");
            if (!string.IsNullOrWhiteSpace(reproSteps))
            {
                sb.AppendLine("\nRepro lépések:");
                sb.AppendLine(ProcessHtmlWithImageMarkers(reproSteps, attachmentMap));
            }

            var ac = GetFieldAsString(workItem, "Microsoft.VSTS.Common.AcceptanceCriteria");
            if (!string.IsNullOrWhiteSpace(ac))
            {
                sb.AppendLine("\nElfogadási kritériumok:");
                sb.AppendLine(ProcessHtmlWithImageMarkers(ac, attachmentMap));
            }

            if (commentsText.Length > 0)
            {
                sb.AppendLine(commentsText.ToString());
            }

            return (sb.ToString(), downloadedImages);
        }

        private async Task<AttachedImage?> DownloadAttachmentAsync(HttpClient client, string url, string fileName, int workItemId)
        {
            try
            {
                _logService.LogInfo("ADO", $"Letöltés indítása: {fileName}");
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) 
                {
                    _logService.LogError("ADO", $"Hiba a letöltéskor ({resp.StatusCode}): {fileName}");
                    return null;
                }

                // Tartalomtípus ellenőrzése a fejlécből
                var contentType = resp.Content.Headers.ContentType?.MediaType;
                if (contentType == null || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    _logService.LogInfo("ADO", $"Kihagyva: Nem kép típusú tartalom ({contentType}): {fileName}");
                    return null;
                }

                var contentLength = resp.Content.Headers.ContentLength ?? 0;
                _logService.LogInfo("ADO", $"Fájl mérete: {contentLength} byte. ({fileName})");
                
                if (contentLength > 30 * 1024 * 1024) 
                {
                    _logService.LogWarning("ADO", $"Túl nagy fájl (>30MB), letöltés megszakítva: {fileName}");
                    return null;
                }

                var bytes = await resp.Content.ReadAsByteArrayAsync();
                _logService.LogInfo("ADO", $"Sikeresen letöltve: {bytes.Length} byte. ({fileName})");
                
                var ext = Path.GetExtension(fileName).TrimStart('.').ToLower();
                if (string.IsNullOrEmpty(ext)) ext = "png";
                var mime = (ext == "png") ? "image/png" : "image/jpeg";

                var cacheDir = Path.Combine(Microsoft.Maui.Storage.FileSystem.CacheDirectory, "ado_attachments", workItemId.ToString());
                Directory.CreateDirectory(cacheDir);
                var localPath = Path.Combine(cacheDir, fileName);
                await File.WriteAllBytesAsync(localPath, bytes);

                string base64Thumb;
                if (bytes.Length < 10 * 1024 * 1024)
                {
                    base64Thumb = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                }
                else
                {
                    base64Thumb = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";
                }

                return new AttachedImage
                {
                    FilePath = localPath,
                    FileName = fileName,
                    Base64Thumbnail = base64Thumb
                };
            }
            catch { return null; }
        }

        private List<string> ExtractImageUrlsFromHtml(string html)
        {
            var urls = new List<string>();
            if (string.IsNullOrWhiteSpace(html)) return urls;

            var matches = Regex.Matches(html, @"<img[^>]+src=[""']([^""']+)[""'][^>]*>", RegexOptions.IgnoreCase);
            foreach (Match m in matches)
            {
                var url = m.Groups[1].Value;
                // Csak az ADO belső attachment URL-jeit keressük
                if (url.Contains("/_apis/wit/attachments/", StringComparison.OrdinalIgnoreCase))
                {
                    urls.Add(HttpUtility.HtmlDecode(url));
                }
            }
            return urls;
        }

        private string ProcessHtmlWithImageMarkers(string html, Dictionary<string, string> attachmentMap)
        {
            if (string.IsNullOrWhiteSpace(html)) return string.Empty;

            var processedHtml = Regex.Replace(html, @"<img[^>]+src=[""']([^""']+)[""'][^>]*>", m =>
            {
                var src = HttpUtility.UrlDecode(m.Groups[1].Value);
                string? fileName = null;

                foreach (var kvp in attachmentMap)
                {
                    if (src.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        fileName = kvp.Value;
                        break;
                    }
                }

                if (fileName == null) fileName = "beágyazott_kép";

                return $" [KÉP: {fileName}] ";
            }, RegexOptions.IgnoreCase);

            return HtmlToPlainText(processedHtml);
        }

        public async Task DownloadWorkItemsAsync(string orgUrl, string project, string pat, string repoName, string iterationPath, string projectRoot, bool isIncremental, bool onlyMine)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
            {
                throw new InvalidOperationException("Érvénytelen projekt mappa van beállítva.");
            }
            if (string.IsNullOrWhiteSpace(orgUrl)) throw new ArgumentException("A szervezet URL megadása kötelező.");
            if (string.IsNullOrWhiteSpace(project)) throw new ArgumentException("A projekt név megadása kötelező.");
            if (string.IsNullOrWhiteSpace(pat)) throw new ArgumentException("A PAT megadása kötelező.");

            orgUrl = orgUrl.Trim().TrimEnd('/');
            project = project.Trim();
            pat = pat.Trim();
            iterationPath = iterationPath?.Trim() ?? string.Empty;
            
            var encodedProject = Uri.EscapeDataString(project);

            var client = _httpClientFactory.CreateClient("AzureDevOps");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

            var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Task", "User Story", "Bug"
            };

            string? repoId = null;
            if (!string.IsNullOrWhiteSpace(repoName))
            {
                repoId = await GetRepositoryIdAsync(client, orgUrl, project, repoName.Trim());
            }

            var projectFolderName = new DirectoryInfo(projectRoot).Name;
            var targetDir = Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory, projectFolderName, "ado");

            if (!isIncremental && Directory.Exists(targetDir))
            {
                try
                {
                    Directory.Delete(targetDir, true);
                }
                catch { }
            }
            Directory.CreateDirectory(targetDir);

            List<int> workItemIds;

            if (!string.IsNullOrWhiteSpace(repoId))
            {
                var repoLinkedIds = await GetRepositoryLinkedWorkItemIdsAsync(client, orgUrl, project, repoId);
                workItemIds = repoLinkedIds.ToList();
            }
            else
            {
                var safeProjectName = project.Replace("'", "''");
                var queryBuilder = new StringBuilder();
                queryBuilder.Append($"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = '{safeProjectName}'");
                queryBuilder.Append(" AND [System.State] NOT IN ('New', 'To Do', 'Proposed', 'Backlog', 'Ötlet', 'New idea')");
                queryBuilder.Append(" AND [System.WorkItemType] IN ('Task','User Story','Bug')");

                if (onlyMine) queryBuilder.Append(" AND [System.AssignedTo] = @Me");

                if (!string.IsNullOrWhiteSpace(iterationPath))
                {
                    var escapedIterationPath = iterationPath.Replace("'", "''");
                    queryBuilder.Append($" AND [System.IterationPath] UNDER '{escapedIterationPath}'");
                }
                
                if (isIncremental && _appState.AdoLastDownloadDate.HasValue)
                {
                    var dateStr = _appState.AdoLastDownloadDate.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
                    queryBuilder.Append($" AND [System.ChangedDate] > '{dateStr}'");
                }

                var wiqlQuery = new { query = queryBuilder.ToString() };
                var wiqlContent = new StringContent(JsonSerializer.Serialize(wiqlQuery), Encoding.UTF8, "application/json");
                var wiqlResponse = await client.PostAsync($"{orgUrl}/{encodedProject}/_apis/wit/wiql?api-version=6.0", wiqlContent);
                
                if (!wiqlResponse.IsSuccessStatusCode)
                {
                    var errorBody = await wiqlResponse.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"WIQL Query Failed ({wiqlResponse.StatusCode}): {errorBody}");
                }

                var wiqlResult = await JsonSerializer.DeserializeAsync<WiqlResponse>(await wiqlResponse.Content.ReadAsStreamAsync(), _jsonOptions);
                if (wiqlResult == null || !wiqlResult.WorkItems.Any()) return;
                workItemIds = wiqlResult.WorkItems.Select(wi => wi.Id).ToList();
            }

            const int batchSize = 200;
            for (int i = 0; i < workItemIds.Count; i += batchSize)
            {
                var batchIds = workItemIds.Skip(i).Take(batchSize);
                var idsString = string.Join(",", batchIds);
                
                var detailsResponse = await client.GetAsync($"{orgUrl}/{encodedProject}/_apis/wit/workitems?ids={idsString}&$expand=all&api-version=6.0");
                if (!detailsResponse.IsSuccessStatusCode) continue;

                var detailsResult = await JsonSerializer.DeserializeAsync<WorkItemListResponse>(await detailsResponse.Content.ReadAsStreamAsync(), _jsonOptions);
                if (detailsResult == null) continue;

                foreach (var workItem in detailsResult.Value)
                {
                    var type = GetFieldAsString(workItem, "System.WorkItemType");
                    if (!allowedTypes.Contains(type)) continue;

                    var formattedContent = FormatWorkItem(workItem);
                    var title = GetFieldAsString(workItem, "System.Title");
                    var safeType = type.Replace(" ", "");
                    var fileName = SanitizeFileName($"{safeType}_{workItem.Id}_{title}.txt");
                    await File.WriteAllTextAsync(Path.Combine(targetDir, fileName), formattedContent);
                }
            }
        }

        private async Task<HashSet<int>> GetRepositoryLinkedWorkItemIdsAsync(HttpClient client, string orgUrl, string project, string repoId)
        {
            var prIds = await GetAllPullRequestIdsAsync(client, orgUrl, project, repoId);
            var wiIds = new HashSet<int>();
            var encodedProject = Uri.EscapeDataString(project);

            foreach (var prId in prIds)
            {
                var resp = await client.GetAsync($"{orgUrl}/{encodedProject}/_apis/git/repositories/{repoId}/pullRequests/{prId}/workitems?api-version=7.1");
                if (!resp.IsSuccessStatusCode) continue;
                var list = await JsonSerializer.DeserializeAsync<ResourceRefListResponse>(await resp.Content.ReadAsStreamAsync(), _jsonOptions);
                if (list?.Value == null) continue;
                foreach (var rr in list.Value)
                {
                    if (int.TryParse(rr.Id, out var id)) wiIds.Add(id);
                }
            }
            return wiIds;
        }

        private async Task<List<int>> GetAllPullRequestIdsAsync(HttpClient client, string orgUrl, string project, string repoId)
        {
            var result = new List<int>();
            var statuses = new[] { "active", "completed", "abandoned" };
            var encodedProject = Uri.EscapeDataString(project);

            foreach (var status in statuses)
            {
                int skip = 0;
                const int pageSize = 100;
                while (true)
                {
                    var url = $"{orgUrl}/{encodedProject}/_apis/git/repositories/{repoId}/pullrequests?searchCriteria.status={status}&$top={pageSize}&$skip={skip}&api-version=7.1";
                    var resp = await client.GetAsync(url);
                    if (!resp.IsSuccessStatusCode) break;
                    var list = await JsonSerializer.DeserializeAsync<GitPullRequestListResponse>(await resp.Content.ReadAsStreamAsync(), _jsonOptions);
                    var items = list?.Value ?? new List<GitPullRequest>();
                    if (items.Count == 0) break;
                    result.AddRange(items.Select(p => p.PullRequestId));
                    if (items.Count < pageSize) break;
                    skip += items.Count;
                }
            }
            return result.Distinct().ToList();
        }

        private async Task<string?> GetRepositoryIdAsync(HttpClient client, string orgUrl, string project, string repoName)
        {
            var encodedProject = Uri.EscapeDataString(project);
            var response = await client.GetAsync($"{orgUrl}/{encodedProject}/_apis/git/repositories?api-version=6.0");
            
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GetRepositoryIdAsync Failed: {response.StatusCode}");

            var repoList = await JsonSerializer.DeserializeAsync<GitRepositoryListResponse>(
                await response.Content.ReadAsStreamAsync(), _jsonOptions);

            var repository = repoList?.Value.FirstOrDefault(r => r.Name.Equals(repoName, StringComparison.OrdinalIgnoreCase));
            if (repository == null) throw new InvalidOperationException($"A(z) '{repoName}' repository nem található.");

            return repository.Id;
        }

        private string FormatWorkItem(WorkItem item)
        {
            var sb = new StringBuilder();
            var title = GetFieldAsString(item, "System.Title");

            if (!string.IsNullOrWhiteSpace(title))
            {
                sb.AppendLine(title);
                sb.AppendLine(title);
                sb.AppendLine(title);
                sb.AppendLine(); 
            }

            sb.AppendLine($"ID: {item.Id}");
            sb.AppendLine($"Type: {GetFieldAsString(item, "System.WorkItemType")}");
            sb.AppendLine($"State: {GetFieldAsString(item, "System.State")}");
            sb.AppendLine($"Title: {title}");
            sb.AppendLine("---");

            AppendHtmlField(sb, "Description", item, "System.Description");
            AppendHtmlField(sb, "Repro Steps", item, "Microsoft.VSTS.TCM.ReproSteps");
            AppendHtmlField(sb, "Acceptance Criteria", item, "Microsoft.VSTS.Common.AcceptanceCriteria");

            return sb.ToString();
        }

        private void AppendHtmlField(StringBuilder sb, string label, WorkItem item, string fieldName)
        {
            var content = GetFieldAsString(item, fieldName);
            if (!string.IsNullOrWhiteSpace(content))
            {
                sb.AppendLine();
                sb.AppendLine($"{label}:");
                sb.AppendLine(HtmlToPlainText(content));
            }
        }

        private string GetFieldAsString(WorkItem item, string fieldName)
        {
            return item.Fields.TryGetValue(fieldName, out var value) && value is JsonElement element
                ? element.ToString()
                : string.Empty;
        }

        private string HtmlToPlainText(string html)
        {
            if (string.IsNullOrEmpty(html)) return string.Empty;
            var text = HttpUtility.HtmlDecode(html);
            text = Regex.Replace(text, "<style.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<script.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "</div>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "</p>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<br.*?>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "</li>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<.*?>", " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text;
        }

        private static readonly string _invalidChars = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
        private static readonly Regex _invalidFileNameRegex = new Regex($"[{Regex.Escape(_invalidChars)}]", RegexOptions.Compiled);

        private string SanitizeFileName(string fileName)
        {
            var sanitized = _invalidFileNameRegex.Replace(fileName, "");
            return sanitized.Length > 150 ? sanitized.Substring(0, 150) : sanitized;
        }
    }
}