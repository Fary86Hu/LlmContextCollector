using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LlmContextCollector.Models
{
    // Model for WIQL query response (list of IDs)
    public class WiqlResponse
    {
        [JsonPropertyName("workItems")]
        public List<WorkItemReference> WorkItems { get; set; } = new();
    }

    public class WorkItemReference
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
    }

    // Model for batch work item details response
    public class WorkItemListResponse
    {
        [JsonPropertyName("value")]
        public List<WorkItem> Value { get; set; } = new();
    }

    public class WorkItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("fields")]
        public Dictionary<string, object> Fields { get; set; } = new();

        [JsonPropertyName("relations")]
        public List<WorkItemRelation> Relations { get; set; } = new();
    }

    public class WorkItemRelation
    {
        [JsonPropertyName("rel")]
        public string Rel { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
    }

    // Model for Git Repositories list
    public class GitRepositoryListResponse
    {
        [JsonPropertyName("value")]
        public List<GitRepository> Value { get; set; } = new();
    }

    public class GitRepository
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    // Minimal models for PR listing
    public class GitPullRequestListResponse
    {
        [JsonPropertyName("value")]
        public List<GitPullRequest> Value { get; set; } = new();
    }

    public class GitPullRequest
    {
        [JsonPropertyName("pullRequestId")]
        public int PullRequestId { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;
    }

    // Minimal models for PR -> WorkItems mapping
    public class ResourceRefListResponse
    {
        [JsonPropertyName("value")]
        public List<ResourceRef> Value { get; set; } = new();
    }

    public class ResourceRef
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
    }
}
