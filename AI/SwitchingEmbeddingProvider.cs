using LlmContextCollector.AI.Embeddings;
using LlmContextCollector.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LlmContextCollector.AI
{
    public class SwitchingEmbeddingProvider : IEmbeddingProvider
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly AppState _appState;

        public SwitchingEmbeddingProvider(IServiceProvider serviceProvider, AppState appState)
        {
            _serviceProvider = serviceProvider;
            _appState = appState;
        }

        private IEmbeddingProvider CurrentProvider
        {
            get
            {
                if (_appState.UseOllamaEmbeddings)
                {
                    return _serviceProvider.GetRequiredService<OllamaEmbeddingProvider>();
                }

                return _serviceProvider.GetRequiredService<NullEmbeddingProvider>();
            }
        }

        public string ModelIdentifier => CurrentProvider.ModelIdentifier;

        public int? DefaultDimension => CurrentProvider.DefaultDimension;

        public Task<float[]> EmbedAsync(string input, CancellationToken ct = default)
        {
            return CurrentProvider.EmbedAsync(input, ct);
        }

        public Task<float[][]> EmbedBatchAsync(IEnumerable<string> inputs, CancellationToken ct = default)
        {
            return CurrentProvider.EmbedBatchAsync(inputs, ct);
        }
    }
}