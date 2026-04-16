using System.Text.Json.Serialization;

namespace Cafs.Client.Http;

public record FileEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("lastModified")] string LastModified
);

public record LockInfo(
    [property: JsonPropertyName("userId")] int UserId,
    [property: JsonPropertyName("acquiredAt")] string AcquiredAt,
    [property: JsonPropertyName("expiresAt")] string ExpiresAt
);

public record LockStatus(
    [property: JsonPropertyName("locked")] bool Locked,
    [property: JsonPropertyName("lock")] LockInfo? Lock
);

public record ErrorResponse(
    [property: JsonPropertyName("message")] string Message
);

public record MessageResponse(
    [property: JsonPropertyName("message")] string Message
);
