using MarkdownGenQAs.Application.Interfaces.ExternalServices;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Models.Enum;
using MarkdownGenQAs.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Qdrant.Client.Grpc;

namespace MarkdownGenQAs.Infrastructure.ExternalServices;

internal interface IEmbeddingProvider
{
    void Configure(EmbeddingGenerationOptions options, EmbeddingServiceOptions svc);
}

internal sealed class DefaultEmbeddingProvider : IEmbeddingProvider
{
    public void Configure(EmbeddingGenerationOptions options, EmbeddingServiceOptions svc) { }
}

internal sealed class GeminiEmbeddingProvider : IEmbeddingProvider
{
    public void Configure(EmbeddingGenerationOptions options, EmbeddingServiceOptions svc)
    {
        options.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            ["output_dimensionality"] = svc.Dimension
        };
    }
}

public class EmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly IOptions<ExternalServiceOptions> _externalOptions;

    public EmbeddingService(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        IOptions<ExternalServiceOptions> externalOptions)
    {
        _generator = generator;
        _externalOptions = externalOptions;
    }

    public async Task<List<PointStruct>> BuildChunkPointStructsAsync(
        List<ChunkInfo> chunks,
        Dictionary<string, Value> basePayload,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        var texts = chunks.Select(ResolveChunkText).ToList();
        var embedOptions = BuildEmbeddingOptions();
        var embeddings = await _generator.GenerateAsync(texts, embedOptions, cancellationToken);

        var pointStructs = new List<PointStruct>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var (chunkType, textForVector) = ResolveChunkType(chunk);

            var payload = new Dictionary<string, Value>(basePayload);
            payload["chunk_type"] = chunkType;
            payload["content"] = textForVector;

            if (chunkType == "summary")
                payload["contentFullForSummary"] = chunk.Content;

            var pointId = CreateChunkPointId(documentId, i);
            pointStructs.Add(new PointStruct
            {
                Id = new PointId { Uuid = pointId.ToString() },
                Vectors = embeddings[i].Vector.ToArray(),
                Payload = { payload }
            });
        }
        return pointStructs;
    }

    private EmbeddingGenerationOptions? BuildEmbeddingOptions()
    {
        var svc = _externalOptions.Value.EmbeddingService;
        var provider = MapProvider(svc.BaseUrl);

        var options = new EmbeddingGenerationOptions
        {
            Dimensions = svc.Dimension
        };

        provider.Configure(options, svc);
        return options;
    }

    private static IEmbeddingProvider MapProvider(string? baseUrl)
    {
        if (string.IsNullOrEmpty(baseUrl))
            return new DefaultEmbeddingProvider();

        if (baseUrl.Contains("generativelanguage.googleapis.com"))
            return new GeminiEmbeddingProvider();

        return new DefaultEmbeddingProvider();
    }

    private static (string chunkType, string textForVector) ResolveChunkType(ChunkInfo chunk)
    {
        if (chunk.Type == TypeChunk.Table)
            return ("table", chunk.Content);
        if (chunk.NeedsSummary && !string.IsNullOrEmpty(chunk.ContentSummary))
            return ("summary", chunk.ContentSummary);
        return ("text", chunk.Content);
    }

    private static string ResolveChunkText(ChunkInfo chunk)
    {
        if (chunk.Type == TypeChunk.Table) return chunk.Content;
        if (chunk.NeedsSummary && !string.IsNullOrEmpty(chunk.ContentSummary)) return chunk.ContentSummary;
        return chunk.Content;
    }

    private static Guid CreateChunkPointId(Guid documentId, int chunkIndex)
    {
        var docBytes = documentId.ToByteArray();
        var indexBytes = BitConverter.GetBytes(chunkIndex);
        var bytes = new byte[16];
        Array.Copy(docBytes, 0, bytes, 0, 12);
        Array.Copy(indexBytes, 0, bytes, 12, 4);
        return new Guid(bytes);
    }
}
