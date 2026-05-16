namespace MarkdownGenQAs.Options;

public class QdrantOptions
{
    public const string SectionName = "Qdrant";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 6334;
    public bool Https { get; set; } = false;
    public string? ApiKey { get; set; }
    public int GrpcTimeoutSeconds { get; set; } = 30;
    public string? Url { get; set; }
    public EmbeddingOptions Embedding { get; set; } = new();
    public CollectionOptions DefaultCollection { get; set; } = new();
}

public class EmbeddingOptions
{
    public string ModelName { get; set; } = "BAAI/bge-m3";
    public int Dimension { get; set; } = 1024;
    public string Distance { get; set; } = "Cosine";
}

public class CollectionOptions
{
    public uint ShardNumber { get; set; } = 2;
    public uint ReplicationFactor { get; set; } = 1;
    public uint WriteConsistencyFactor { get; set; } = 1;
    public bool OnDiskPayload { get; set; } = false;
    public string? ShardingMethod { get; set; }
}
