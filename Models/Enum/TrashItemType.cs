using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrashItemType
{
    Dataset,
    Folder,
    Document
}
