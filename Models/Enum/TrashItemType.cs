using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrashItemType
{
    OrganizationUnit,
    Dataset,
    Folder,
    Document
}
