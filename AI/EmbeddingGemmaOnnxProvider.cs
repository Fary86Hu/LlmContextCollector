using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.DotNet;

namespace LlmContextCollector.AI.Embeddings
{
    public sealed class EmbeddingGemmaOnnxProvider : IEmbeddingProvider, IDisposable
    {
        private readonly InferenceSession _session;
        private readonly Tokenizer _tokenizer;
        private readonly int _maxLen;
        private readonly string _inputIdsName;
        private readonly string? _attMaskName;
        private readonly string _outputName;
        private int? _dim;
        public string ModelIdentifier { get; }

        public EmbeddingGemmaOnnxProvider(
            string onnxPath,
            Tokenizer tokenizer,
            int maxLen = 2048,
            bool useDml = true,
            int threads = 1,
            string? inputIdsName = null,
            string? attentionMaskName = null,
            string? outputName = null)
        {
            var so = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = threads,
                InterOpNumThreads = threads,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
            };
            if (useDml)
            {
                try { so.AppendExecutionProvider_DML(); } catch { }
            }

            _session = new InferenceSession(onnxPath, so);
            _tokenizer = tokenizer;
            ModelIdentifier = $"gemma-onnx-{Path.GetFileNameWithoutExtension(onnxPath)}";

            _maxLen = maxLen;

            var inKeys = _session.InputMetadata.Keys.ToArray();
            var outKeys = _session.OutputMetadata.Keys.ToArray();

            _inputIdsName = inputIdsName
                            ?? (inKeys.FirstOrDefault(k => k == "input_ids")
                                ?? inKeys.First());

            _attMaskName = attentionMaskName
                           ?? inKeys.FirstOrDefault(k => k == "attention_mask");

            _outputName = outputName
                          ?? (outKeys.FirstOrDefault(k => k == "sentence_embedding")
                              ?? outKeys.FirstOrDefault(k => k == "last_hidden_state")
                              ?? outKeys.First());
        }

        public int? DefaultDimension
        {
            get
            {
                if (_dim != null) return _dim;
                var md = _session.OutputMetadata[_outputName];
                var dims = md.Dimensions;
                _dim = dims.Length switch
                {
                    3 => dims[2] > 0 ? dims[2] : null,
                    2 => dims[1] > 0 ? dims[1] : null,
                    _ => null
                };
                return _dim;
            }
        }

        public Task<float[]> EmbedAsync(string input, CancellationToken ct = default)
        {
            return EmbedBatchAsync(new[] { input }, ct).ContinueWith(t => t.Result[0], ct);
        }

        public Task<float[][]> EmbedBatchAsync(IEnumerable<string> inputs, CancellationToken ct = default)
        {
            var texts = inputs.ToArray();
            if (texts.Length == 0) return Task.FromResult(Array.Empty<float[]>());

            var tokenIds = new List<uint[]>();
            var maxSeq = 0;
            foreach (var t in texts)
            {
                var ids = _tokenizer.Encode(t);
                if (ids.Length > _maxLen) ids = ids.Take(_maxLen).ToArray();
                tokenIds.Add(ids);
                if (ids.Length > maxSeq) maxSeq = ids.Length;
            }
            if (maxSeq == 0) return Task.FromResult(texts.Select(_ => Array.Empty<float>()).ToArray());

            var bsz = texts.Length;
            var inputIds = new DenseTensor<long>(new[] { bsz, maxSeq });
            DenseTensor<long>? attMask = null;

            if (_attMaskName != null && _session.InputMetadata.ContainsKey(_attMaskName))
                attMask = new DenseTensor<long>(new[] { bsz, maxSeq });

            for (int b = 0; b < bsz; b++)
            {
                var ids = tokenIds[b];
                for (int i = 0; i < maxSeq; i++)
                {
                    inputIds[b, i] = i < ids.Length ? ids[i] : 0;
                    if (attMask != null) attMask[b, i] = i < ids.Length ? 1 : 0;
                }
            }

            var nv = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputIdsName, inputIds)
            };
            if (attMask != null)
                nv.Add(NamedOnnxValue.CreateFromTensor(_attMaskName!, attMask));

            float[][] result;
            using (var outputs = _session.Run(nv))
            {
                var outVal = outputs.FirstOrDefault(o => o.Name == _outputName) ?? outputs.First();
                var tensor = outVal.AsTensor<float>();
                var dims = tensor.Dimensions.ToArray();

                if (dims.Length == 2)
                {
                    var H = dims[1];
                    result = new float[bsz][];
                    for (int b = 0; b < bsz; b++)
                    {
                        var v = new float[H];
                        for (int h = 0; h < H; h++) v[h] = tensor[b, h];
                        L2NormalizeInPlace(v);
                        result[b] = v;
                    }
                }
                else if (dims.Length == 3)
                {
                    var L = dims[1];
                    var H = dims[2];
                    result = new float[bsz][];

                    for (int b = 0; b < bsz; b++)
                    {
                        var v = new float[H];
                        var valid = 0;
                        for (int i = 0; i < L; i++)
                        {
                            if (attMask == null || attMask[b, i] == 1)
                            {
                                for (int h = 0; h < H; h++)
                                    v[h] += tensor[b, i, h];
                                valid++;
                            }
                        }
                        if (valid > 0)
                        {
                            var inv = 1f / valid;
                            for (int h = 0; h < H; h++) v[h] *= inv;
                        }
                        L2NormalizeInPlace(v);
                        result[b] = v;
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Váratlan kimeneti shape: [{string.Join(",", dims)}]");
                }
            }

            return Task.FromResult(result);
        }

        public void Dispose() => _session.Dispose();

        private static void L2NormalizeInPlace(float[] v)
        {
            double s = 0;
            for (int i = 0; i < v.Length; i++) s += v[i] * v[i];
            var n = (float)Math.Sqrt(s);
            if (n > 0f) for (int i = 0; i < v.Length; i++) v[i] /= n;
        }
    }
}