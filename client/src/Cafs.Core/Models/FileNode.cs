using System.Text.Json.Serialization;

namespace Cafs.Core.Models;

public record FileNode(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("lastModified")] string LastModified
);
