using MarkdownGenQAs.Models;
using Qdrant.Client.Grpc;

namespace MarkdownGenQAs.Application.Interfaces.ExternalServices;

public interface IEmbeddingService
{
    Task<List<PointStruct>> BuildChunkPointStructsAsync(
        List<ChunkInfo> chunks,
        Dictionary<string, Value> basePayload,
        Guid documentId,
        CancellationToken cancellationToken = default);
}
