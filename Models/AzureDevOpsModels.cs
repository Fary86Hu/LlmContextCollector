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
        [JsonConverter(typeof(Utils.StringOrIntConverter))]
        public string Id { get; set; } = string.Empty;
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

        [JsonPropertyName("attributes")]
        public Dictionary<string, object> Attributes { get; set; } = new();
    }

    public class WorkItemCommentListResponse
    {
        [JsonPropertyName("comments")]
        public List<WorkItemComment> Comments { get; set; } = new();
    }

    public class WorkItemComment
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("createdBy")]
        public WorkItemUser? CreatedBy { get; set; }

        [JsonPropertyName("createdDate")]
        public DateTime CreatedDate { get; set; }
    }

    public class WorkItemUser
    {
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;
    }

    public class GitPullRequest
    {
        [JsonPropertyName("pullRequestId")]
        public int PullRequestId { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("sourceRefName")]
        public string SourceRefName { get; set; } = string.Empty;

        [JsonPropertyName("targetRefName")]
        public string TargetRefName { get; set; } = string.Empty;

        [JsonPropertyName("repository")]
        public GitRepository? Repository { get; set; }
    }

    public class GitRepository
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    public class GitPullRequestIterationListResponse
    {
        [JsonPropertyName("value")]
        public List<GitPullRequestIteration> Value { get; set; } = new();
    }

    public class GitPullRequestIteration
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;
    }

    public class GitPullRequestChangeListResponse
    {
        [JsonPropertyName("changeEntries")]
        public List<GitPullRequestChange> ChangeEntries { get; set; } = new();
    }

    public class GitPullRequestChange
    {
        [JsonPropertyName("changeId")]
        public int ChangeId { get; set; }

        [JsonPropertyName("item")]
        public GitItem? Item { get; set; }

        [JsonPropertyName("changeType")]
        public string ChangeType { get; set; } = string.Empty;
    }

    public class GitItem
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;
    }

    public class GitPullRequestCommentThreadListResponse
    {
        [JsonPropertyName("value")]
        public List<GitPullRequestCommentThread> Value { get; set; } = new();
    }

    public class GitPullRequestCommentThread
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("comments")]
        public List<WorkItemComment> Comments { get; set; } = new();

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("threadContext")]
        public GitPullRequestCommentThreadContext? ThreadContext { get; set; }
    }

    public class GitPullRequestCommentThreadContext
    {
        [JsonPropertyName("filePath")]
        public string Path { get; set; } = string.Empty;
    }

    public class GitPullRequestWorkItemReferenceListResponse
    {
        [JsonPropertyName("value")]
        public List<WorkItemReference> Value { get; set; } = new();
    }
}
