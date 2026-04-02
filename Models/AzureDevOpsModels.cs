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
}
