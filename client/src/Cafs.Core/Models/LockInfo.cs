using System.Text.Json.Serialization;

namespace Cafs.Core.Models;

public record LockInfo(
    [property: JsonPropertyName("userId")] int UserId,
    [property: JsonPropertyName("acquiredAt")] string AcquiredAt,
    [property: JsonPropertyName("expiresAt")] string ExpiresAt
);
