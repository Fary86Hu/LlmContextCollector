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

        public record PullRequestFile(string Path, string ChangeType);
        public record PullRequestData(string FormattedText, List<PullRequestFile> Files, string SourceBranch, List<AttachedImage> Images);
        public record LinkedPrInfo(int Id, string Title, string SourceBranch, List<PullRequestFile> Files);

        public async Task<PullRequestData> GetFormattedPullRequestAsync(int prId)
        {
            _logService.LogInfo("ADO", $"Pull Request {prId} lekérése indítva...");

            if (string.IsNullOrWhiteSpace(_appState.AzureDevOpsOrganizationUrl) || string.IsNullOrWhiteSpace(_appState.AzureDevOpsPat))
            {
                throw new InvalidOperationException("Az Azure DevOps beállítások (URL, PAT) nincsenek megadva.");
            }

            var orgUrl = _appState.AzureDevOpsOrganizationUrl.Trim().TrimEnd('/');
            var project = _appState.AzureDevOpsProject.Trim();
            var pat = _appState.AzureDevOpsPat.Trim();
            var encodedProject = Uri.EscapeDataString(project);

            var client = _httpClientFactory.CreateClient("AzureDevOps");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

            var prUrl = $"{orgUrl}/{encodedProject}/_apis/git/pullrequests/{prId}?api-version=6.0";
            var prResponse = await client.GetAsync(prUrl);
            if (!prResponse.IsSuccessStatusCode)
            {
                var err = await prResponse.Content.ReadAsStringAsync();
                _logService.LogError("ADO", $"PR hiba ({prResponse.StatusCode})", err);
                throw new HttpRequestException($"Nem sikerült lekérni a Pull Requestet ({prId}).");
            }

            var pr = await JsonSerializer.DeserializeAsync<GitPullRequest>(await prResponse.Content.ReadAsStreamAsync(), _jsonOptions);
            if (pr == null) return new PullRequestData(string.Empty, new List<PullRequestFile>(), string.Empty, new List<AttachedImage>());

            var repositoryId = pr.Repository?.Id;
            _logService.LogInfo("ADO", $"PR alapadatok lekérve. Repo: {pr.Repository?.Name}");

            // Részadatok lekérése (szálak, iterációk, munkaminták)
            async Task<T?> FetchSafe<T>(string url, string label) where T : class
            {
                try {
                    var resp = await client.GetAsync(url);
                    if (!resp.IsSuccessStatusCode) return null;
                    return await JsonSerializer.DeserializeAsync<T>(await resp.Content.ReadAsStreamAsync(), _jsonOptions);
                } catch (Exception ex) {
                    _logService.LogWarning("ADO", $"Részadat hiba ({label})", ex.Message);
                    return null;
                }
            }

            var threadsData = await FetchSafe<GitPullRequestCommentThreadListResponse>(
                $"{orgUrl}/_apis/git/repositories/{repositoryId}/pullRequests/{prId}/threads?api-version=6.0", "threads");

            var iterationsData = await FetchSafe<GitPullRequestIterationListResponse>(
                $"{orgUrl}/_apis/git/repositories/{repositoryId}/pullRequests/{prId}/iterations?api-version=6.0", "iterations");

            var lastIterationId = iterationsData?.Value.LastOrDefault()?.Id ?? 1;
            var changesData = await FetchSafe<GitPullRequestChangeListResponse>(
                $"{orgUrl}/_apis/git/repositories/{repositoryId}/pullRequests/{prId}/iterations/{lastIterationId}/changes?api-version=6.0", "changes");

            var workItemsData = await FetchSafe<GitPullRequestWorkItemReferenceListResponse>(
                $"{orgUrl}/_apis/git/repositories/{repositoryId}/pullRequests/{prId}/workitems?api-version=6.0", "workitems");

            var affectedFiles = new List<PullRequestFile>();
            var allImages = new List<AttachedImage>();
            var sb = new StringBuilder();
            sb.AppendLine($"# ADO Pull Request {prId}: {pr.Title}");
            sb.AppendLine($"Forrás: {pr.SourceRefName} -> Cél: {pr.TargetRefName}");
            sb.AppendLine("\n## Leírás:");
            sb.AppendLine(pr.Description);

            if (threadsData?.Value != null)
            {
                var threadsWithComments = threadsData.Value.Where(t => t.Comments != null && t.Comments.Any(c => !string.IsNullOrWhiteSpace(c.Text))).ToList();
                if (threadsWithComments.Any())
                {
                    sb.AppendLine("\n## Megjegyzések és szálak:");
                    foreach (var thread in threadsWithComments)
                    {
                        var firstComment = thread.Comments.First();
                        var context = thread.ThreadContext != null ? $" (Fájl: {thread.ThreadContext.Path})" : "";
                        sb.AppendLine($"\n[{thread.Status}]{context} {firstComment.CreatedBy?.DisplayName}:");
                        sb.AppendLine(HtmlToPlainText(firstComment.Text));
                        foreach (var reply in thread.Comments.Skip(1)) sb.AppendLine($"  > {reply.CreatedBy?.DisplayName}: {HtmlToPlainText(reply.Text)}");
                    }
                }
            }

            if (changesData?.ChangeEntries != null)
            {
                sb.AppendLine("\n## Módosított fájlok:");
                foreach (var change in changesData.ChangeEntries.Where(c => c.Item != null))
                {
                    sb.AppendLine($"- [{change.ChangeType}] {change.Item!.Path}");
                    affectedFiles.Add(new PullRequestFile(change.Item.Path, change.ChangeType));
                }
            }

            var branchName = pr.SourceRefName;
            if (branchName.StartsWith("refs/heads/")) branchName = branchName.Substring(11);

            if (workItemsData?.Value != null && workItemsData.Value.Any())
            {
                sb.AppendLine("\n## Kapcsolódó Work Item-ek:");
                foreach (var wiRef in workItemsData.Value)
                {
                    if (int.TryParse(wiRef.Id, out int wiId))
                    {
                        try {
                            var wiData = await GetFormattedWorkItemAsync(wiId, allImages.Count);
                            sb.AppendLine($"\n--- START WORK ITEM {wiId} ---");
                            sb.AppendLine(wiData.Text);
                            if (wiData.Images != null) allImages.AddRange(wiData.Images);
                            sb.AppendLine($"--- END WORK ITEM {wiId} ---");
                        } catch (Exception ex) {
                            sb.AppendLine($"\n[HIBA: Nem sikerült letölteni a(z) {wiId} munkamintát: {ex.Message}]");
                        }
                    }
                }
            }

            sb.AppendLine("\n--- END OF PULL REQUEST CONTEXT ---");
            _logService.LogInfo("ADO", $"PR {prId} feldolgozása kész. Fájlok: {affectedFiles.Count}");
            return new PullRequestData(sb.ToString(), affectedFiles, branchName, allImages);
        }

        public async Task<(string Text, List<AttachedImage> Images, int FailedImagesCount)> GetFormattedWorkItemAsync(int workItemId, int startIndex)
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

            var attachmentClient = _httpClientFactory.CreateClient("AzureDevOpsAttachment");
            attachmentClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
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
                return (string.Empty, new List<AttachedImage>(), 0);
            }

            // 1. Kommentek lekérése
            var commentsUrl = $"{orgUrl}/{encodedProject}/_apis/wit/workitems/{workItemId}/comments?api-version=6.0-preview.3";
            var commentsResponse = await client.GetAsync(commentsUrl);
            var commentsData = commentsResponse.IsSuccessStatusCode
                ? await JsonSerializer.DeserializeAsync<WorkItemCommentListResponse>(await commentsResponse.Content.ReadAsStreamAsync(), _jsonOptions)
                : null;

            // 2. SZIGORÚ SORREND MEGHATÁROZÁSA
            // Összegyűjtjük az összes kép URL-t abban a sorrendben, ahogy a szövegben megjelennének
            var orderedImageUrls = new List<string>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddUrlsFromHtml(string? html)
            {
                if (string.IsNullOrWhiteSpace(html)) return;
                var found = ExtractImageUrlsFromHtml(html);
                foreach (var u in found)
                {
                    if (seenUrls.Add(u)) orderedImageUrls.Add(u);
                }
            }

            AddUrlsFromHtml(GetFieldAsString(workItem, "System.Description"));
            AddUrlsFromHtml(GetFieldAsString(workItem, "Microsoft.VSTS.TCM.ReproSteps"));
            AddUrlsFromHtml(GetFieldAsString(workItem, "Microsoft.VSTS.Common.AcceptanceCriteria"));

            if (commentsData?.Comments != null)
            {
                foreach (var c in commentsData.Comments.OrderBy(c => c.CreatedDate))
                {
                    AddUrlsFromHtml(c.Text);
                }
            }

            // Csatolmányok (amik esetleg nem voltak inline beágyazva)
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
                    if (seenUrls.Add(rel.Url)) orderedImageUrls.Add(rel.Url);
                }
            }

            // 3. LETÖLTÉS A MEGHATÁROZOTT SORRENDBEN
            var urlToAttachedImageMap = new Dictionary<string, AttachedImage>();
            var urlToIndexMap = new Dictionary<string, int>();
            var failedImageIndexes = new HashSet<int>();
            var finalImagesToReturn = new List<AttachedImage>();
            int failedImagesCount = 0;

            for (int i = 0; i < orderedImageUrls.Count; i++)
            {
                var currentUrl = orderedImageUrls[i];
                var index = startIndex + i + 1;
                urlToIndexMap[currentUrl] = index;

                var fileNameMatch = Regex.Match(currentUrl, @"fileName=([^&]+)");
                var originalName = fileNameMatch.Success ? HttpUtility.UrlDecode(fileNameMatch.Groups[1].Value) : $"image_{index}.png";
                var urlId = Regex.Match(currentUrl, @"/attachments/([^/?#]+)").Groups[1].Value;
                if (string.IsNullOrEmpty(urlId)) urlId = Guid.NewGuid().ToString("N").Substring(0, 8);

                var uniqueFileNameForStorage = $"{workItemId}_{urlId}_{originalName}";

                var downloaded = await DownloadAttachmentAsync(attachmentClient, currentUrl, uniqueFileNameForStorage, workItemId);
                if (downloaded != null)
                {
                    // A FileName a UI-ban maradjon meg, de a sorrend a lényeg
                    urlToAttachedImageMap[currentUrl] = downloaded;
                    finalImagesToReturn.Add(downloaded);
                }
                else
                {
                    failedImageIndexes.Add(index);
                    failedImagesCount++;
                }
            }

            // 4. SZÖVEG ÖSSZEÁLLÍTÁSA AZ INDEXEKKEL
            var commentsText = new StringBuilder();
            if (commentsData?.Comments != null && commentsData.Comments.Any())
            {
                commentsText.AppendLine("\nKözösségi megjegyzések / Kommentek:");
                foreach (var comment in commentsData.Comments.OrderBy(c => c.CreatedDate))
                {
                    commentsText.AppendLine($"[{comment.CreatedDate:yyyy-MM-dd HH:mm}] {comment.CreatedBy?.DisplayName}:");
                    commentsText.AppendLine(ProcessHtmlWithImageMarkers(comment.Text, urlToIndexMap, failedImageIndexes));
                    commentsText.AppendLine("---");
                }
            }

            var sb = new StringBuilder();
            var title = GetFieldAsString(workItem, "System.Title");
            sb.AppendLine($"# ADO Work Item {workItemId}: {title}");
            sb.AppendLine($"Típus: {GetFieldAsString(workItem, "System.WorkItemType")}");
            sb.AppendLine($"Állapot: {GetFieldAsString(workItem, "System.State")}");

            sb.AppendLine("\nLeírás:");
            sb.AppendLine(ProcessHtmlWithImageMarkers(GetFieldAsString(workItem, "System.Description"), urlToIndexMap, failedImageIndexes));

            var reproSteps = GetFieldAsString(workItem, "Microsoft.VSTS.TCM.ReproSteps");
            if (!string.IsNullOrWhiteSpace(reproSteps))
            {
                sb.AppendLine("\nRepro lépések:");
                sb.AppendLine(ProcessHtmlWithImageMarkers(reproSteps, urlToIndexMap, failedImageIndexes));
            }

            var ac = GetFieldAsString(workItem, "Microsoft.VSTS.Common.AcceptanceCriteria");
            if (!string.IsNullOrWhiteSpace(ac))
            {
                sb.AppendLine("\nElfogadási kritériumok:");
                sb.AppendLine(ProcessHtmlWithImageMarkers(ac, urlToIndexMap, failedImageIndexes));
            }

            if (commentsText.Length > 0)
            {
                sb.AppendLine(commentsText.ToString());
            }

            return (sb.ToString(), finalImagesToReturn, failedImagesCount);
        }

        public async Task<LinkedPrInfo?> GetPullRequestDetailsAsync(int prId)
        {
            if (string.IsNullOrWhiteSpace(_appState.AzureDevOpsOrganizationUrl) || string.IsNullOrWhiteSpace(_appState.AzureDevOpsPat))
                return null;

            var orgUrl = _appState.AzureDevOpsOrganizationUrl.Trim().TrimEnd('/');
            var project = _appState.AzureDevOpsProject.Trim();
            var pat = _appState.AzureDevOpsPat.Trim();
            var encodedProject = Uri.EscapeDataString(project);

            var client = _httpClientFactory.CreateClient("AzureDevOps");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

            var prUrl = $"{orgUrl}/{encodedProject}/_apis/git/pullrequests/{prId}?api-version=6.0";
            var prResponse = await client.GetAsync(prUrl);
            if (!prResponse.IsSuccessStatusCode) return null;

            var pr = await JsonSerializer.DeserializeAsync<GitPullRequest>(await prResponse.Content.ReadAsStreamAsync(), _jsonOptions);
            if (pr == null) return null;

            var repositoryId = pr.Repository?.Id;
            var lastIterationId = 1;

            var iterationsUrl = $"{orgUrl}/_apis/git/repositories/{repositoryId}/pullRequests/{prId}/iterations?api-version=6.0";
            var iterResponse = await client.GetAsync(iterationsUrl);
            if (iterResponse.IsSuccessStatusCode)
            {
                var iterationsData = await JsonSerializer.DeserializeAsync<GitPullRequestIterationListResponse>(await iterResponse.Content.ReadAsStreamAsync(), _jsonOptions);
                lastIterationId = iterationsData?.Value.LastOrDefault()?.Id ?? 1;
            }

            var affectedFiles = new List<PullRequestFile>();
            var changesUrl = $"{orgUrl}/_apis/git/repositories/{repositoryId}/pullRequests/{prId}/iterations/{lastIterationId}/changes?api-version=6.0";
            var changesResponse = await client.GetAsync(changesUrl);
            if (changesResponse.IsSuccessStatusCode)
            {
                var changesData = await JsonSerializer.DeserializeAsync<GitPullRequestChangeListResponse>(await changesResponse.Content.ReadAsStreamAsync(), _jsonOptions);
                if (changesData?.ChangeEntries != null)
                {
                    foreach (var change in changesData.ChangeEntries.Where(c => c.Item != null))
                    {
                        affectedFiles.Add(new PullRequestFile(change.Item!.Path, change.ChangeType));
                    }
                }
            }

            var branchName = pr.SourceRefName;
            if (branchName.StartsWith("refs/heads/")) branchName = branchName.Substring(11);

            return new LinkedPrInfo(prId, pr.Title, branchName, affectedFiles);
        }

        public async Task<WorkItem?> GetRawWorkItemAsync(int workItemId)
        {
            if (string.IsNullOrWhiteSpace(_appState.AzureDevOpsOrganizationUrl) || string.IsNullOrWhiteSpace(_appState.AzureDevOpsPat))
                return null;

            var orgUrl = _appState.AzureDevOpsOrganizationUrl.Trim().TrimEnd('/');
            var project = _appState.AzureDevOpsProject.Trim();
            var pat = _appState.AzureDevOpsPat.Trim();
            var encodedProject = Uri.EscapeDataString(project);

            var client = _httpClientFactory.CreateClient("AzureDevOps");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

            var url = $"{orgUrl}/{encodedProject}/_apis/wit/workitems/{workItemId}?$expand=all&api-version=6.0";
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            return await JsonSerializer.DeserializeAsync<WorkItem>(await response.Content.ReadAsStreamAsync(), _jsonOptions);
        }

        public async Task<bool> UpdateWorkItemFieldsAsync(
            int workItemId, 
            string? state, 
            double? remainingWork, 
            double? completedWork,
            double? originalEstimate,
            double? storyPoints,
            int? priority,
            string? severity,
            DateTime? targetDate)
        {
            if (string.IsNullOrWhiteSpace(_appState.AzureDevOpsOrganizationUrl) || string.IsNullOrWhiteSpace(_appState.AzureDevOpsPat))
                return false;

            var orgUrl = _appState.AzureDevOpsOrganizationUrl.Trim().TrimEnd('/');
            var project = _appState.AzureDevOpsProject.Trim();
            var pat = _appState.AzureDevOpsPat.Trim();
            var encodedProject = Uri.EscapeDataString(project);

            var client = _httpClientFactory.CreateClient("AzureDevOps");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

            var patchList = new List<object>();
            if (!string.IsNullOrEmpty(state))
            {
                patchList.Add(new { op = "add", path = "/fields/System.State", value = state });
            }

            patchList.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Scheduling.RemainingWork", value = remainingWork });
            patchList.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Scheduling.CompletedWork", value = completedWork });
            patchList.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Scheduling.OriginalEstimate", value = originalEstimate });
            patchList.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Scheduling.StoryPoints", value = storyPoints });

            if (priority.HasValue)
            {
                patchList.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Common.Priority", value = priority.Value });
            }
            else
            {
                patchList.Add(new { op = "remove", path = "/fields/Microsoft.VSTS.Common.Priority" });
            }

            patchList.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Common.Severity", value = severity ?? "" });

            if (targetDate.HasValue)
            {
                patchList.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Scheduling.TargetDate", value = targetDate.Value.ToString("yyyy-MM-ddTHH:mm:ssZ") });
            }
            else
            {
                patchList.Add(new { op = "remove", path = "/fields/Microsoft.VSTS.Scheduling.TargetDate" });
            }

            var url = $"{orgUrl}/{encodedProject}/_apis/wit/workitems/{workItemId}?api-version=6.0";
            var content = new StringContent(JsonSerializer.Serialize(patchList), Encoding.UTF8, "application/json-patch+json");

            var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = content };
            var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                _logService.LogError("ADO", $"Hiba a WI #{workItemId} mezoinak frissitesekor", err);
                return false;
            }

            return true;
        }

        public async Task<bool> AddWorkItemCommentAsync(int workItemId, string commentText)
        {
            if (string.IsNullOrWhiteSpace(_appState.AzureDevOpsOrganizationUrl) || string.IsNullOrWhiteSpace(_appState.AzureDevOpsPat))
                return false;

            var orgUrl = _appState.AzureDevOpsOrganizationUrl.Trim().TrimEnd('/');
            var project = _appState.AzureDevOpsProject.Trim();
            var pat = _appState.AzureDevOpsPat.Trim();
            var encodedProject = Uri.EscapeDataString(project);

            var client = _httpClientFactory.CreateClient("AzureDevOps");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

            var url = $"{orgUrl}/{encodedProject}/_apis/wit/workitems/{workItemId}/comments?api-version=6.0-preview.3";
            var body = new { text = commentText };
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var response = await client.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                _logService.LogError("ADO", $"Hiba komment hozzaadasakor #{workItemId}", err);
                return false;
            }

            return true;
        }

        private async Task<AttachedImage?> DownloadAttachmentAsync(HttpClient client, string url, string fileName, int workItemId)
        {
            HttpResponseMessage? resp = null;
            try
            {
                _logService.LogInfo("ADO", $"Letöltés indítása: {fileName}");
                resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

                if (resp.StatusCode == System.Net.HttpStatusCode.Found ||
                    resp.StatusCode == System.Net.HttpStatusCode.MovedPermanently ||
                    resp.StatusCode == System.Net.HttpStatusCode.Redirect ||
                    resp.StatusCode == System.Net.HttpStatusCode.TemporaryRedirect)
                {
                    var redirectUrl = resp.Headers.Location;
                    if (redirectUrl != null)
                    {
                        var absoluteRedirectUrl = redirectUrl.IsAbsoluteUri ? redirectUrl : new Uri(new Uri(url), redirectUrl);
                        _logService.LogInfo("ADO", $"Átirányítás követése (Auth nélkül): {absoluteRedirectUrl}");
                        using var noAuthClient = _httpClientFactory.CreateClient();
                        resp.Dispose();
                        resp = await noAuthClient.GetAsync(absoluteRedirectUrl, HttpCompletionOption.ResponseHeadersRead);
                    }
                }

                if (!resp.IsSuccessStatusCode)
                {
                    _logService.LogError("ADO", $"Hiba a letöltéskor ({resp.StatusCode}): {fileName}");
                    return null;
                }

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
            catch (Exception ex)
            {
                _logService.LogError("ADO", $"Kivétel a letöltéskor: {ex.Message}");
                return null;
            }
            finally
            {
                resp?.Dispose();
            }
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

        private string ProcessHtmlWithImageMarkers(string html, Dictionary<string, int> urlToIndexMap, HashSet<int> failedImageIndexes)
        {
            if (string.IsNullOrWhiteSpace(html)) return string.Empty;

            var processedHtml = Regex.Replace(html, @"<img[^>]+src=[""']([^""']+)[""'][^>]*>", m =>
            {
                var src = HttpUtility.UrlDecode(m.Groups[1].Value);
                int index = -1;

                foreach (var kvp in urlToIndexMap)
                {
                    if (src.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        index = kvp.Value;
                        break;
                    }
                }

                if (index == -1) return " [KÉP: ismeretlen] ";

                if (failedImageIndexes.Contains(index)) return $" [KÉP: {index} - LETÖLTÉS SIKERTELEN] ";

                return $" [KÉP: {index}] ";
            }, RegexOptions.IgnoreCase);

            return HtmlToPlainText(processedHtml);
        }

        public async Task<List<AdoIdentity>> GetProjectMembersAsync()
        {
            if (string.IsNullOrWhiteSpace(_appState.AzureDevOpsOrganizationUrl) || string.IsNullOrWhiteSpace(_appState.AzureDevOpsPat)) return new();

            var orgUrl = _appState.AzureDevOpsOrganizationUrl.Trim().TrimEnd('/');
            var project = _appState.AzureDevOpsProject.Trim();
            var pat = _appState.AzureDevOpsPat.Trim();
            var client = _httpClientFactory.CreateClient("AzureDevOps");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var teamsResp = await client.GetAsync($"{orgUrl}/_apis/projects/{Uri.EscapeDataString(project)}/teams?api-version=6.0", cts.Token);
                if (!teamsResp.IsSuccessStatusCode) return new();
                var teams = await JsonSerializer.DeserializeAsync<AdoTeamListResponse>(await teamsResp.Content.ReadAsStreamAsync(cts.Token), _jsonOptions, cts.Token);

                var allMembers = new Dictionary<string, AdoIdentity>();
                foreach (var team in teams?.Value ?? new())
                {
                    using var memberCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        var membersResp = await client.GetAsync($"{orgUrl}/_apis/projects/{Uri.EscapeDataString(project)}/teams/{team.Id}/members?api-version=6.0", memberCts.Token);
                        if (membersResp.IsSuccessStatusCode)
                        {
                            var members = await JsonSerializer.DeserializeAsync<AdoMemberListResponse>(await membersResp.Content.ReadAsStreamAsync(memberCts.Token), _jsonOptions, memberCts.Token);
                            foreach (var m in members?.Value ?? new())
                            {
                                if (m.Identity != null && !allMembers.ContainsKey(m.Identity.UniqueName))
                                    allMembers[m.Identity.UniqueName] = m.Identity;
                            }
                        }
                    }
                    catch
                    {
                    }
                }
                return allMembers.Values.OrderBy(x => x.DisplayName).ToList();
            }
            catch (Exception ex)
            {
                _logService.LogError("ADO", "Hiba a projekt tagok lekérésekor", ex.Message);
                return new();
            }
        }

        public async Task<List<WorkItemSearchResult>> SearchWorkItemsAsync(IEnumerable<string> states, IEnumerable<string> assignedTo, string? type)
        {
            if (string.IsNullOrWhiteSpace(_appState.AzureDevOpsOrganizationUrl) || string.IsNullOrWhiteSpace(_appState.AzureDevOpsPat))
            {
                throw new InvalidOperationException("Az Azure DevOps beállítások hiányoznak.");
            }

            var orgUrl = _appState.AzureDevOpsOrganizationUrl.Trim().TrimEnd('/');
            var project = _appState.AzureDevOpsProject.Trim();
            var pat = _appState.AzureDevOpsPat.Trim();
            var encodedProject = Uri.EscapeDataString(project);

            var client = _httpClientFactory.CreateClient("AzureDevOps");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

            var queryBuilder = new StringBuilder();
            queryBuilder.Append($"SELECT [System.Id], [System.Title], [System.WorkItemType], [System.State], [System.AssignedTo] FROM WorkItems WHERE [System.TeamProject] = '{project.Replace("'", "''")}'");

            if (_appState.AdoDownloadOnlyMine)
            {
                queryBuilder.Append(" AND [System.State] IN ('Resolved', 'Closed', 'Done', 'Kész', 'Completed', 'Lezárt')");
            }
            else if (states != null && states.Any())
            {
                var stateList = string.Join(",", states.Select(s => $"'{s.Replace("'", "''")}'"));
                queryBuilder.Append($" AND [System.State] IN ({stateList})");
            }

            if (assignedTo != null && assignedTo.Any())
            {
                var userList = string.Join(",", assignedTo.Select(u => u == "@Me" ? "@Me" : $"'{u.Replace("'", "''")}'"));

                if (_appState.AdoMinChangedDate.HasValue)
                {
                    var minDateStr = _appState.AdoMinChangedDate.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

                    if (_appState.AdoDownloadOnlyMine)
                    {
                        queryBuilder.Append(" AND (");
                        queryBuilder.Append($"([System.AssignedTo] IN ({userList}) AND [System.ChangedDate] >= '{minDateStr}') OR ");
                        queryBuilder.Append($"([Microsoft.VSTS.Common.ResolvedBy] IN ({userList}) AND [Microsoft.VSTS.Common.ResolvedDate] >= '{minDateStr}') OR ");
                        queryBuilder.Append($"([Microsoft.VSTS.Common.ClosedBy] IN ({userList}) AND [Microsoft.VSTS.Common.ClosedDate] >= '{minDateStr}')");
                        queryBuilder.Append(")");
                    }
                    else
                    {
                        queryBuilder.Append($" AND [System.AssignedTo] IN ({userList}) AND [System.ChangedDate] >= '{minDateStr}'");
                    }
                }
                else
                {
                    if (_appState.AdoDownloadOnlyMine)
                    {
                        queryBuilder.Append($" AND ([System.AssignedTo] IN ({userList}) OR [Microsoft.VSTS.Common.ResolvedBy] IN ({userList}) OR [Microsoft.VSTS.Common.ClosedBy] IN ({userList}))");
                    }
                    else
                    {
                        queryBuilder.Append($" AND [System.AssignedTo] IN ({userList})");
                    }
                }
            }
            else
            {
                if (_appState.AdoMinChangedDate.HasValue)
                {
                    var minDateStr = _appState.AdoMinChangedDate.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    queryBuilder.Append($" AND [System.ChangedDate] >= '{minDateStr}'");
                }
            }

            if (!string.IsNullOrWhiteSpace(type))
                queryBuilder.Append($" AND [System.WorkItemType] = '{type.Replace("'", "''")}'");

            queryBuilder.Append(" ORDER BY [System.ChangedDate] DESC");

            var wiqlQuery = new { query = queryBuilder.ToString() };
            var wiqlContent = new StringContent(JsonSerializer.Serialize(wiqlQuery), Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"{orgUrl}/{encodedProject}/_apis/wit/wiql?api-version=6.0", wiqlContent);

            if (!response.IsSuccessStatusCode) return new List<WorkItemSearchResult>();

            var wiqlResult = await JsonSerializer.DeserializeAsync<WiqlResponse>(await response.Content.ReadAsStreamAsync(), _jsonOptions);
            if (wiqlResult == null || !wiqlResult.WorkItems.Any()) return new List<WorkItemSearchResult>();

            var ids = wiqlResult.WorkItems.Take(50).Select(wi => wi.Id);
            var detailsResponse = await client.GetAsync($"{orgUrl}/{encodedProject}/_apis/wit/workitems?ids={string.Join(",", ids)}&fields=System.Id,System.Title,System.WorkItemType,System.State,System.AssignedTo&api-version=6.0");

            if (!detailsResponse.IsSuccessStatusCode) return new List<WorkItemSearchResult>();

            var detailsResult = await JsonSerializer.DeserializeAsync<WorkItemListResponse>(await detailsResponse.Content.ReadAsStreamAsync(), _jsonOptions);
            return detailsResult?.Value.Select(wi => new WorkItemSearchResult(
                wi.Id,
                wi.Fields.TryGetValue("System.Title", out var t) ? t.ToString()! : "",
                wi.Fields.TryGetValue("System.WorkItemType", out var ty) ? ty.ToString()! : "",
                wi.Fields.TryGetValue("System.State", out var s) ? s.ToString()! : "",
                wi.Fields.TryGetValue("System.AssignedTo", out var a) ? (a is JsonElement e && e.TryGetProperty("displayName", out var d) ? d.GetString()! : a.ToString()!) : "Unassigned"
            )).ToList() ?? new List<WorkItemSearchResult>();
        }

        public async Task DownloadWorkItemsAsync(string orgUrl, string project, string pat, string iterationPath, string projectRoot, bool isIncremental, bool onlyMine)
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

            var safeProjectName = project.Replace("'", "''");
            var queryBuilder = new StringBuilder();
            queryBuilder.Append($"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = '{safeProjectName}'");
            queryBuilder.Append(" AND [System.State] NOT IN ('New', 'To Do', 'Proposed', 'Backlog', 'Ötlet', 'New idea')");
            queryBuilder.Append(" AND [System.WorkItemType] IN ('Task','User Story','Bug')");

            if (onlyMine)
            {
                if (!isIncremental && _appState.AdoMinChangedDate.HasValue)
                {
                    var minDateStr = _appState.AdoMinChangedDate.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    queryBuilder.Append(" AND (");
                    queryBuilder.Append($"([System.AssignedTo] = @Me AND [System.ChangedDate] >= '{minDateStr}') OR ");
                    queryBuilder.Append($"([Microsoft.VSTS.Common.ResolvedBy] = @Me AND [Microsoft.VSTS.Common.ResolvedDate] >= '{minDateStr}') OR ");
                    queryBuilder.Append($"([Microsoft.VSTS.Common.ClosedBy] = @Me AND [Microsoft.VSTS.Common.ClosedDate] >= '{minDateStr}')");
                    queryBuilder.Append(")");
                }
                else
                {
                    queryBuilder.Append(" AND ([System.AssignedTo] = @Me OR [Microsoft.VSTS.Common.ResolvedBy] = @Me OR [Microsoft.VSTS.Common.ClosedBy] = @Me)");
                }
            }
            else
            {
                if (!isIncremental && _appState.AdoMinChangedDate.HasValue)
                {
                    var minDateStr = _appState.AdoMinChangedDate.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    queryBuilder.Append($" AND [System.ChangedDate] >= '{minDateStr}'");
                }
            }

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

            workItemIds = wiqlResult.WorkItems
                .Select(wi => int.TryParse(wi.Id, out int id) ? id : 0)
                .Where(id => id > 0)
                .ToList();

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

        private async Task<List<GitCommitInfo>> RunGitLogCommandAsync(string projectRoot, string arguments)
        {
            var commits = new List<GitCommitInfo>();
            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot)) return commits;

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = projectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null) return commits;

                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (string.IsNullOrWhiteSpace(output)) return commits;

                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                GitCommitInfo? currentCommit = null;

                foreach (var line in lines)
                {
                    if (line.StartsWith("COMMIT:"))
                    {
                        var parts = line.Substring(7).Split('|');
                        if (parts.Length >= 4)
                        {
                            currentCommit = new GitCommitInfo(
                                parts[0],
                                parts[1],
                                parts[2],
                                parts[3],
                                new List<GitFileStat>()
                            );
                            commits.Add(currentCommit);
                        }
                    }
                    else
                    {
                        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3 && currentCommit != null)
                        {
                            if (int.TryParse(parts[0], out int added) && int.TryParse(parts[1], out int deleted))
                            {
                                var filePath = parts[2];
                                currentCommit.Files.Add(new GitFileStat(filePath, added, deleted));
                            }
                        }
                    }
                }
            }
            catch
            {
            }
            return commits;
        }

        private static bool IsActualCodeFile(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var codeExtensions = new HashSet<string> { ".cs", ".razor", ".js", ".ts", ".py", ".go", ".rs", ".cpp", ".h", ".fs", ".kt", ".java" };
            return codeExtensions.Contains(ext);
        }

        private async Task<string> RunGitCommandAsync(string projectRoot, string arguments)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot)) return string.Empty;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = projectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null) return string.Empty;
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                return output.Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private async Task<string> GetLocalGitUserNameAsync(string projectRoot)
        {
            return await RunGitCommandAsync(projectRoot, "config user.name");
        }

        private static bool IsAuthorMatch(string gitAuthor, IEnumerable<string> assignedTo, string localGitUser)
        {
            if (assignedTo == null || !assignedTo.Any()) return true;
            foreach (var assignee in assignedTo)
            {
                if (assignee == "@Me" && !string.IsNullOrEmpty(localGitUser))
                {
                    if (gitAuthor.Contains(localGitUser, StringComparison.OrdinalIgnoreCase) || localGitUser.Contains(gitAuthor, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                var cleanAssignee = assignee;
                var parenIndex = assignee.IndexOf('(');
                if (parenIndex != -1) cleanAssignee = assignee.Substring(0, parenIndex).Trim();

                if (gitAuthor.Contains(cleanAssignee, StringComparison.OrdinalIgnoreCase) || cleanAssignee.Contains(gitAuthor, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private async Task<List<GitCommitInfo>> GetCommitWithDiffStatsAsync(string projectRoot, string hash, IEnumerable<string> assignedTo, string localGitUser)
        {
            var commits = new List<GitCommitInfo>();
            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot)) return commits;

            try
            {
                var infoOutput = await RunGitCommandAsync(projectRoot, $"log -1 {hash} --pretty=format:\"COMMIT:%h|%an|%ad|%s\"");
                if (string.IsNullOrWhiteSpace(infoOutput) || !infoOutput.StartsWith("COMMIT:")) return commits;

                var parts = infoOutput.Substring(7).Split('|');
                if (parts.Length < 4) return commits;

                var author = parts[1];
                if (!IsAuthorMatch(author, assignedTo, localGitUser)) return commits;

                var commit = new GitCommitInfo(
                    parts[0],
                    parts[1],
                    parts[2],
                    parts[3],
                    new List<GitFileStat>()
                );

                var diffOutput = await RunGitCommandAsync(projectRoot, $"diff {hash}^1 {hash} --numstat");
                if (!string.IsNullOrWhiteSpace(diffOutput))
                {
                    var lines = diffOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        var fileParts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (fileParts.Length >= 3)
                        {
                            if (int.TryParse(fileParts[0], out int added) && int.TryParse(fileParts[1], out int deleted))
                            {
                                commit.Files.Add(new GitFileStat(fileParts[2], added, deleted));
                            }
                        }
                    }
                }

                commits.Add(commit);
            }
            catch
            {
            }
            return commits;
        }

        private async Task<GitTaskActivity?> GetGitActivityForTaskAsync(string projectRoot, int taskId, List<string> linkedBranches, List<string> linkedCommits, List<int> linkedPrIds, IEnumerable<string> assignedTo, string localGitUser)
        {
            var commits = new List<GitCommitInfo>();
            var fileStats = new List<GitFileStat>();

            foreach (var prId in linkedPrIds)
            {
                var grepOutput = await RunGitCommandAsync(projectRoot, $"log --all --grep=\"PR {prId}\" --grep=\"PR #{prId}\" --grep=\"PR#{prId}\" --grep=\"Pull Request {prId}\" --grep=\"pull request {prId}\" --pretty=format:\"%h|%an|%ad|%s\"");
                if (!string.IsNullOrWhiteSpace(grepOutput))
                {
                    var lines = grepOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('|');
                        if (parts.Length >= 4)
                        {
                            var author = parts[1];
                            if (IsAuthorMatch(author, assignedTo, localGitUser))
                            {
                                commits.Add(new GitCommitInfo(parts[0], parts[1], parts[2], parts[3], new List<GitFileStat>()));
                            }
                        }
                    }
                }
            }

            foreach (var branch in linkedBranches)
            {
                if (branch.Equals("main", StringComparison.OrdinalIgnoreCase) ||
                    branch.Equals("master", StringComparison.OrdinalIgnoreCase) ||
                    branch.Equals("develop", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var target in new[] { "main", "develop", "master" })
                {
                    var authorsOutput = await RunGitCommandAsync(projectRoot, $"log origin/{target}..origin/{branch} --pretty=format:\"%an\"");
                    if (string.IsNullOrWhiteSpace(authorsOutput))
                    {
                        authorsOutput = await RunGitCommandAsync(projectRoot, $"log {target}..{branch} --pretty=format:\"%an\"");
                    }

                    if (!string.IsNullOrWhiteSpace(authorsOutput))
                    {
                        var authors = authorsOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Distinct();
                        var hasMatch = authors.Any(author => IsAuthorMatch(author, assignedTo, localGitUser));

                        if (hasMatch)
                        {
                            var diffArgs = $"diff origin/{target}...origin/{branch} --numstat";
                            var diffOutput = await RunGitCommandAsync(projectRoot, diffArgs);
                            if (string.IsNullOrWhiteSpace(diffOutput))
                            {
                                diffOutput = await RunGitCommandAsync(projectRoot, $"diff {target}...{branch} --numstat");
                            }

                            if (!string.IsNullOrWhiteSpace(diffOutput))
                            {
                                var diffLines = diffOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                                foreach (var dl in diffLines)
                                {
                                    var fileParts = dl.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                                    if (fileParts.Length >= 3)
                                    {
                                        if (int.TryParse(fileParts[0], out int added) && int.TryParse(fileParts[1], out int deleted))
                                        {
                                            fileStats.Add(new GitFileStat(fileParts[2], added, deleted));
                                        }
                                    }
                                }
                            }
                            break;
                        }
                    }
                }
            }

            foreach (var hash in linkedCommits)
            {
                var commitInfo = await GetCommitWithDiffStatsAsync(projectRoot, hash, assignedTo, localGitUser);
                if (commitInfo.Any())
                {
                    commits.AddRange(commitInfo);
                    fileStats.AddRange(commitInfo.SelectMany(c => c.Files));
                }
            }

            if (!fileStats.Any())
            {
                var grepOutputTask = await RunGitCommandAsync(projectRoot, $"log --all --grep=\"{taskId}\" --pretty=format:\"%h\"");
                if (!string.IsNullOrWhiteSpace(grepOutputTask))
                {
                    var hashes = grepOutputTask.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var hash in hashes)
                    {
                        var commitInfo = await GetCommitWithDiffStatsAsync(projectRoot, hash, assignedTo, localGitUser);
                        if (commitInfo.Any())
                        {
                            commits.AddRange(commitInfo);
                            fileStats.AddRange(commitInfo.SelectMany(c => c.Files));
                        }
                    }
                }
            }

            if (!fileStats.Any() && !commits.Any()) return null;

            var codeAdded = 0;
            var codeDeleted = 0;
            var otherAdded = 0;
            var otherDeleted = 0;

            var codeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var otherFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var totalAdded = 0;
            var totalDeleted = 0;
            var totalFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var uniqueStats = fileStats
                .GroupBy(s => s.Path)
                .Select(g => new GitFileStat(g.Key, g.Max(x => x.Added), g.Max(x => x.Deleted)))
                .ToList();

            foreach (var file in uniqueStats)
            {
                totalFiles.Add(file.Path);
                totalAdded += file.Added;
                totalDeleted += file.Deleted;

                if (IsActualCodeFile(file.Path))
                {
                    codeFiles.Add(file.Path);
                    codeAdded += file.Added;
                    codeDeleted += file.Deleted;
                }
                else
                {
                    otherFiles.Add(file.Path);
                    otherAdded += file.Added;
                    otherDeleted += file.Deleted;
                }
            }

            var uniqueCommits = commits.GroupBy(c => c.Hash).Select(g => g.First()).ToList();

            return new GitTaskActivity(
                uniqueCommits,
                totalAdded,
                totalDeleted,
                totalFiles.Count,
                codeAdded,
                codeDeleted,
                codeFiles.Count,
                otherAdded,
                otherDeleted,
                otherFiles.Count
            );
        }

        public async Task<AdoReportResult?> GenerateWorkReportAsync(IEnumerable<string> assignedTo, DateTime? minDate)
        {
            if (string.IsNullOrWhiteSpace(_appState.AzureDevOpsOrganizationUrl) || string.IsNullOrWhiteSpace(_appState.AzureDevOpsPat))
            {
                throw new InvalidOperationException("Az Azure DevOps beállítások hiányoznak.");
            }

            var orgUrl = _appState.AzureDevOpsOrganizationUrl.Trim().TrimEnd('/');
            var project = _appState.AzureDevOpsProject.Trim();
            var pat = _appState.AzureDevOpsPat.Trim();
            var encodedProject = Uri.EscapeDataString(project);

            var client = _httpClientFactory.CreateClient("AzureDevOps");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

            var queryBuilder = new StringBuilder();
            queryBuilder.Append($"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = '{project.Replace("'", "''")}'");
            queryBuilder.Append(" AND [System.State] IN ('Resolved', 'Closed', 'Done', 'Kész', 'Lezárt', 'Completed')");

            if (assignedTo != null && assignedTo.Any())
            {
                var userList = string.Join(",", assignedTo.Select(u => u == "@Me" ? "@Me" : $"'{u.Replace("'", "''")}'"));
                queryBuilder.Append($" AND ([System.AssignedTo] IN ({userList}) OR [Microsoft.VSTS.Common.ResolvedBy] IN ({userList}) OR [Microsoft.VSTS.Common.ClosedBy] IN ({userList}))");
            }

            if (minDate.HasValue)
            {
                var minDateStr = minDate.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                queryBuilder.Append($" AND [System.ChangedDate] >= '{minDateStr}'");
            }

            queryBuilder.Append(" ORDER BY [System.ChangedDate] DESC");

            var wiqlQuery = new { query = queryBuilder.ToString() };
            var wiqlContent = new StringContent(JsonSerializer.Serialize(wiqlQuery), Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"{orgUrl}/{encodedProject}/_apis/wit/wiql?api-version=6.0", wiqlContent);

            if (!response.IsSuccessStatusCode) return null;

            var wiqlResult = await JsonSerializer.DeserializeAsync<WiqlResponse>(await response.Content.ReadAsStreamAsync(), _jsonOptions);
            if (wiqlResult == null || !wiqlResult.WorkItems.Any()) return new AdoReportResult(new(), "Nincs lezárt vagy feloldott feladat a megadott időszakban.", "[]");

            var localGitUser = await GetLocalGitUserNameAsync(_appState.ProjectRoot);
            var ids = wiqlResult.WorkItems.Select(wi => wi.Id).ToList();
            var idsToFetch = ids.Take(200).ToList();
            var detailsResponse = await client.GetAsync($"{orgUrl}/{encodedProject}/_apis/wit/workitems?ids={string.Join(",", idsToFetch)}&$expand=relations&api-version=6.0");

            if (!detailsResponse.IsSuccessStatusCode) return null;

            var detailsResult = await JsonSerializer.DeserializeAsync<WorkItemListResponse>(await detailsResponse.Content.ReadAsStreamAsync(), _jsonOptions);
            if (detailsResult == null) return null;

            var items = new List<AdoReportItem>();
            foreach (var wi in detailsResult.Value)
            {
                double completedWork = 0;
                double storyPoints = 0;
                double originalEstimate = 0;

                if (wi.Fields.TryGetValue("Microsoft.VSTS.Scheduling.CompletedWork", out var cwVal) && cwVal is JsonElement cwElem && cwElem.ValueKind == JsonValueKind.Number)
                    completedWork = cwElem.GetDouble();

                if (wi.Fields.TryGetValue("Microsoft.VSTS.Scheduling.StoryPoints", out var spVal) && spVal is JsonElement spElem && spElem.ValueKind == JsonValueKind.Number)
                    storyPoints = spElem.GetDouble();

                if (wi.Fields.TryGetValue("Microsoft.VSTS.Scheduling.OriginalEstimate", out var oeVal) && oeVal is JsonElement oeElem && oeElem.ValueKind == JsonValueKind.Number)
                    originalEstimate = oeElem.GetDouble();

                var assignedToName = "Unassigned";
                if (wi.Fields.TryGetValue("System.AssignedTo", out var a))
                {
                    assignedToName = a is JsonElement e && e.TryGetProperty("displayName", out var d) ? d.GetString()! : a.ToString()!;
                }

                var linkedBranches = new List<string>();
                var linkedCommits = new List<string>();
                var linkedPrIds = new List<int>();

                if (wi.Relations != null)
                {
                    foreach (var rel in wi.Relations)
                    {
                        if (rel.Rel == "ArtifactLink")
                        {
                            var decodedUrl = Uri.UnescapeDataString(rel.Url);
                            if (decodedUrl.Contains("PullRequestId", StringComparison.OrdinalIgnoreCase))
                            {
                                var lastSlash = decodedUrl.LastIndexOf('/');
                                if (lastSlash != -1 && int.TryParse(decodedUrl.Substring(lastSlash + 1), out var prId))
                                {
                                    linkedPrIds.Add(prId);
                                    var prInfo = await GetPullRequestDetailsAsync(prId);
                                    if (prInfo != null && !string.IsNullOrEmpty(prInfo.SourceBranch))
                                    {
                                        linkedBranches.Add(prInfo.SourceBranch);
                                    }
                                }
                            }
                            else if (decodedUrl.Contains("Commit", StringComparison.OrdinalIgnoreCase))
                            {
                                var lastSlash = decodedUrl.LastIndexOf('/');
                                if (lastSlash != -1)
                                {
                                    linkedCommits.Add(decodedUrl.Substring(lastSlash + 1));
                                }
                            }
                        }
                    }
                }

                var gitActivity = await GetGitActivityForTaskAsync(_appState.ProjectRoot, wi.Id, linkedBranches, linkedCommits, linkedPrIds, assignedTo, localGitUser);

                items.Add(new AdoReportItem(
                    wi.Id,
                    wi.Fields.TryGetValue("System.Title", out var t) ? t.ToString()! : "",
                    wi.Fields.TryGetValue("System.WorkItemType", out var ty) ? ty.ToString()! : "",
                    wi.Fields.TryGetValue("System.State", out var s) ? s.ToString()! : "",
                    assignedToName,
                    completedWork,
                    storyPoints,
                    originalEstimate,
                    gitActivity
                ));
            }

            var sb = new StringBuilder();
            sb.AppendLine("=== AZURE DEVOPS ÉS GIT INTEGRÁLT MUNKA RIPORT ===");
            if (minDate.HasValue)
            {
                sb.AppendLine($"Időszak kezdete: {minDate.Value:yyyy-MM-dd}");
            }
            sb.AppendLine($"Generálva: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            var developers = items
                .Where(i => i.GitActivity != null)
                .SelectMany(i => i.GitActivity!.Commits.Select(c => c.Author))
                .Distinct()
                .ToList();

            sb.AppendLine("ÖSSZESÍTÉS GIT AKTIVITÁS ALAPJÁN (FEJLESZTŐK):");
            sb.AppendLine("--------------------------------------------------");
            if (developers.Any())
            {
                foreach (var dev in developers)
                {
                    var devCommits = items
                        .Where(i => i.GitActivity != null)
                        .SelectMany(i => i.GitActivity!.Commits.Where(c => c.Author.Equals(dev, StringComparison.OrdinalIgnoreCase)))
                        .ToList();

                    var devTasks = items
                        .Where(i => i.GitActivity != null && i.GitActivity.Commits.Any(c => c.Author.Equals(dev, StringComparison.OrdinalIgnoreCase)))
                        .ToList();

                    var codeAdded = 0;
                    var codeDeleted = 0;
                    var codeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    var otherAdded = 0;
                    var otherDeleted = 0;
                    var otherFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var commit in devCommits)
                    {
                        foreach (var file in commit.Files)
                        {
                            if (IsActualCodeFile(file.Path))
                            {
                                codeFiles.Add(file.Path);
                                codeAdded += file.Added;
                                codeDeleted += file.Deleted;
                            }
                            else
                            {
                                otherFiles.Add(file.Path);
                                otherAdded += file.Added;
                                otherDeleted += file.Deleted;
                            }
                        }
                    }

                    sb.AppendLine($"Fejlesztő (Git szerző): {dev}");
                    sb.AppendLine($"  - Érintett feladatok száma: {devTasks.Count} db");
                    sb.AppendLine($"  - Összes commit száma: {devCommits.Count} db");
                    sb.AppendLine($"  - VALÓS KÓD VÁLTOZTATÁSOK (.cs, .razor, .js, .ts stb.):");
                    sb.AppendLine($"    * Módosított kód fájlok: {codeFiles.Count} db");
                    sb.AppendLine($"    * Kódváltozás: +{codeAdded} / -{codeDeleted} sor");
                    sb.AppendLine($"  - EGYÉB VÁLTOZTATÁSOK (szöveg, json, erőforrások, stílusok stb.):");
                    sb.AppendLine($"    * Módosított egyéb fájlok: {otherFiles.Count} db");
                    sb.AppendLine($"    * Egyéb változás: +{otherAdded} / -{otherDeleted} sor");
                    sb.AppendLine();
                }
            }
            else
            {
                sb.AppendLine("Nem található Git aktivitás (egyik feladat ID-ja sem szerepel a commit üzenetekben).");
                sb.AppendLine();
            }

            sb.AppendLine("ÖSSZESÍTÉS ADO FELELŐSÖK SZERINT (A FEJLÉCEK ALAPJÁN):");
            sb.AppendLine("--------------------------------------------------");
            var grouped = items.GroupBy(i => i.AssignedTo).ToList();
            foreach (var group in grouped)
            {
                var count = group.Count();
                var totalCompleted = group.Sum(g => g.CompletedWork);
                var totalSp = group.Sum(g => g.StoryPoints);
                var totalOriginal = group.Sum(g => g.OriginalEstimate);

                sb.AppendLine($"Felelős: {group.Key}");
                sb.AppendLine($"  - Elvégzett feladatok száma: {count} db");
                sb.AppendLine($"  - ADO-ba rögzített munkaóra (Completed Work): {totalCompleted} óra");
                sb.AppendLine($"  - ADO Story Point: {totalSp} SP");
                sb.AppendLine();
            }

            sb.AppendLine("RÉSZLETES FELADAT ÉS KÓDVÁLTOZTATÁS LISTA:");
            sb.AppendLine("--------------------------------------------------");
            foreach (var item in items)
            {
                sb.AppendLine($"#{item.Id} [{item.Type}] {item.Title}");
                sb.AppendLine($"  ADO Státusz: {item.State} | Felelős: {item.AssignedTo}");
                sb.AppendLine($"  ADO Logolt óra: {item.CompletedWork} óra | Story Points: {item.StoryPoints}");

                if (item.GitActivity != null)
                {
                    sb.AppendLine("  Git Aktivitás:");
                    sb.AppendLine($"    - Kód fájlok (.cs, .razor, .js...): {item.GitActivity.CodeFilesTouched} db (+{item.GitActivity.CodeAdded} / -{item.GitActivity.CodeDeleted} sor)");
                    sb.AppendLine($"    - Egyéb fájlok (.json, .resx...): {item.GitActivity.OtherFilesTouched} db (+{item.GitActivity.OtherAdded} / -{item.GitActivity.OtherDeleted} sor)");
                    sb.AppendLine("    - Commitek részletei:");
                    foreach (var commit in item.GitActivity.Commits)
                    {
                        sb.AppendLine($"      * [{commit.Hash}] {commit.Author}: {commit.Subject}");
                    }
                }
                else
                {
                    sb.AppendLine("  Git Aktivitás: Nem található a feladathoz kapcsolt commit.");
                }
                sb.AppendLine();
            }

            var reportData = new
            {
                Metadata = new
                {
                    ReportType = "ADO_Git_Integrated_Report",
                    GeneratedAt = DateTime.UtcNow.ToString("o"),
                    MinDate = minDate,
                    AssigneesFilter = assignedTo
                },
                Summary = grouped.Select(g => new
                {
                    Assignee = g.Key,
                    TaskCount = g.Count(),
                    TotalCompletedWorkHours = g.Sum(x => x.CompletedWork),
                    TotalStoryPoints = g.Sum(x => x.StoryPoints),
                    TotalOriginalEstimateHours = g.Sum(x => x.OriginalEstimate)
                }).ToList(),
                GitSummary = developers.Select(dev => {
                    var devCommits = items
                        .Where(i => i.GitActivity != null)
                        .SelectMany(i => i.GitActivity!.Commits.Where(c => c.Author.Equals(dev, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    return new {
                        Developer = dev,
                        CommitCount = devCommits.Count,
                        LinesAdded = devCommits.Sum(c => c.Files.Sum(f => f.Added)),
                        LinesDeleted = devCommits.Sum(c => c.Files.Sum(f => f.Deleted))
                    };
                }).ToList(),
                Details = items.Select(i => new
                {
                    i.Id,
                    i.Title,
                    i.Type,
                    i.State,
                    i.AssignedTo,
                    i.CompletedWork,
                    i.StoryPoints,
                    i.OriginalEstimate,
                    GitActivity = i.GitActivity != null ? new {
                        CommitCount = i.GitActivity.Commits.Count,
                        TotalAdded = i.GitActivity.TotalAdded,
                        TotalDeleted = i.GitActivity.TotalDeleted,
                        FilesTouchedCount = i.GitActivity.TotalFilesTouched,
                        CodeAdded = i.GitActivity.CodeAdded,
                        CodeDeleted = i.GitActivity.CodeDeleted,
                        CodeFilesTouched = i.GitActivity.CodeFilesTouched,
                        OtherAdded = i.GitActivity.OtherAdded,
                        OtherDeleted = i.GitActivity.OtherDeleted,
                        OtherFilesTouched = i.GitActivity.OtherFilesTouched
                    } : null
                }).ToList()
            };

            var jsonOptionsWithWrite = new JsonSerializerOptions { WriteIndented = true };
            var jsonData = JsonSerializer.Serialize(reportData, jsonOptionsWithWrite);

            sb.AppendLine("=== AI ELEMZÉSRE ELŐKÉSZÍTETT RAW DATA (JSON) ===");
            sb.AppendLine("Az alábbi JSON formátumú adatok segítségével az AI pontos és részletes elemzést végezhet a csapat teljesítményéről és a tényleges kódváltoztatásokról:");
            sb.AppendLine("```json");
            sb.AppendLine(jsonData);
            sb.AppendLine("```");

            return new AdoReportResult(items, sb.ToString(), jsonData);
        }

        private string SanitizeFileName(string fileName)
        {
            var sanitized = _invalidFileNameRegex.Replace(fileName, "");
            return sanitized.Length > 150 ? sanitized.Substring(0, 150) : sanitized;
        }
    }
}