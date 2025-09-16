using LlmContextCollector.Models;
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
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        public AzureDevOpsService(IHttpClientFactory httpClientFactory, AppState appState)
        {
            _httpClientFactory = httpClientFactory;
            _appState = appState;
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

        public async Task DownloadWorkItemsAsync(string orgUrl, string project, string pat, string repoName, string iterationPath, string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
            {
                throw new InvalidOperationException("Érvénytelen projekt mappa van beállítva.");
            }

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
                repoId = await GetRepositoryIdAsync(client, orgUrl, project, repoName);
            }

            var projectFolderName = new DirectoryInfo(projectRoot).Name;
            var targetDir = Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory, projectFolderName, "ado");

            if (Directory.Exists(targetDir))
            {
                // Próbáljuk meg a meglévő fájlokat törölni, de ne álljon le hibával, ha nem sikerül
                try
                {
                    Directory.Delete(targetDir, true);
                }
                catch (Exception ex)
                {
                     System.Diagnostics.Debug.WriteLine($"Could not delete old ADO directory: {ex.Message}");
                }
            }
            Directory.CreateDirectory(targetDir);

            List<int> workItemIds;

            if (!string.IsNullOrWhiteSpace(repoId))
            {
                var repoLinkedIds = await GetRepositoryLinkedWorkItemIdsAsync(client, orgUrl, project, repoId);
                if (repoLinkedIds.Count == 0)
                {
                    return;
                }
                workItemIds = repoLinkedIds.ToList();
            }
            else
            {
                var queryBuilder = new StringBuilder();
                queryBuilder.Append($"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = '{project}'");
                queryBuilder.Append(" AND [System.State] NOT IN ('New', 'To Do', 'Proposed', 'Backlog', 'Ötlet', 'New idea')");
                queryBuilder.Append(" AND [System.WorkItemType] IN ('Task','User Story','Bug')");

                if (!string.IsNullOrWhiteSpace(iterationPath))
                {
                    var escapedIterationPath = iterationPath.Replace("'", "''");
                    queryBuilder.Append($" AND [System.IterationPath] UNDER '{escapedIterationPath}'");
                }

                var wiqlQuery = new
                {
                    query = queryBuilder.ToString()
                };
                var wiqlContent = new StringContent(JsonSerializer.Serialize(wiqlQuery), Encoding.UTF8, "application/json");
                var wiqlResponse = await client.PostAsync($"{orgUrl}/{project}/_apis/wit/wiql?api-version=6.0", wiqlContent);
                wiqlResponse.EnsureSuccessStatusCode();

                var wiqlResult = await JsonSerializer.DeserializeAsync<WiqlResponse>(await wiqlResponse.Content.ReadAsStreamAsync(), _jsonOptions);
                if (wiqlResult == null || !wiqlResult.WorkItems.Any())
                {
                    return;
                }
                workItemIds = wiqlResult.WorkItems.Select(wi => wi.Id).ToList();
            }

            const int batchSize = 200;
            for (int i = 0; i < workItemIds.Count; i += batchSize)
            {
                var batchIds = workItemIds.Skip(i).Take(batchSize);
                var idsString = string.Join(",", batchIds);
                var detailsResponse = await client.GetAsync($"{orgUrl}/{project}/_apis/wit/workitems?ids={idsString}&$expand=all&api-version=6.0");
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
            foreach (var prId in prIds)
            {
                var resp = await client.GetAsync($"{orgUrl}/{project}/_apis/git/repositories/{repoId}/pullRequests/{prId}/workitems?api-version=7.1");
                if (!resp.IsSuccessStatusCode) continue;
                var list = await JsonSerializer.DeserializeAsync<ResourceRefListResponse>(await resp.Content.ReadAsStreamAsync(), _jsonOptions);
                if (list?.Value == null) continue;
                foreach (var rr in list.Value)
                {
                    if (int.TryParse(rr.Id, out var id))
                    {
                        wiIds.Add(id);
                    }
                }
            }
            return wiIds;
        }

        private async Task<List<int>> GetAllPullRequestIdsAsync(HttpClient client, string orgUrl, string project, string repoId)
        {
            var result = new List<int>();
            var statuses = new[] { "active", "completed", "abandoned" };
            foreach (var status in statuses)
            {
                int skip = 0;
                const int pageSize = 100;
                while (true)
                {
                    var url = $"{orgUrl}/{project}/_apis/git/repositories/{repoId}/pullrequests?searchCriteria.status={status}&$top={pageSize}&$skip={skip}&api-version=7.1";
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
            var response = await client.GetAsync($"{orgUrl}/{project}/_apis/git/repositories?api-version=6.0");
            response.EnsureSuccessStatusCode();

            var repoList = await JsonSerializer.DeserializeAsync<GitRepositoryListResponse>(
                await response.Content.ReadAsStreamAsync(), _jsonOptions);

            var repository = repoList?.Value.FirstOrDefault(r => r.Name.Equals(repoName, StringComparison.OrdinalIgnoreCase));

            if (repository == null)
            {
                throw new InvalidOperationException($"A(z) '{repoName}' repository nem található a(z) '{project}' projektben.");
            }

            return repository.Id;
        }

        private string FormatWorkItem(WorkItem item)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"ID: {item.Id}");
            sb.AppendLine($"Type: {GetFieldAsString(item, "System.WorkItemType")}");
            sb.AppendLine($"State: {GetFieldAsString(item, "System.State")}");
            sb.AppendLine($"Title: {GetFieldAsString(item, "System.Title")}");
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

        private string SanitizeFileName(string fileName)
        {
            var invalidChars = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
            var regex = new Regex($"[{Regex.Escape(invalidChars)}]");
            var sanitized = regex.Replace(fileName, "");
            return sanitized.Length > 150 ? sanitized.Substring(0, 150) : sanitized;
        }
    }
}