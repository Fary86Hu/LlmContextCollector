namespace LlmContextCollector.Models
{
    public class ExclusionRule
    {
        public string Pattern { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;

        public ExclusionRule Clone() => new ExclusionRule { Pattern = this.Pattern, IsEnabled = this.IsEnabled };
    }
}