namespace LlmContextCollector.Models
{
    public class PromptTemplate
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;

        // Klónozáshoz, hogy a szerkesztés ne hasson ki az eredeti listára
        public PromptTemplate Clone() => (PromptTemplate)this.MemberwiseClone();
    }
}