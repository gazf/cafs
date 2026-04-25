using System.Text.Json.Serialization;

namespace Cafs.Transport;

public record ErrorResponse(
    [property: JsonPropertyName("message")] string Message
);
