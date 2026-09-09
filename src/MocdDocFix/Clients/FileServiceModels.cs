using System.Text.Json.Serialization;

namespace MocdDocFix.Clients;

public sealed record ApiResponse<T>(
    [property: JsonPropertyName("Success")] bool Success,
    [property: JsonPropertyName("Message")] string? Message,
    [property: JsonPropertyName("Data")] T? Data,
    [property: JsonPropertyName("Errors")] List<string>? Errors)
{
    public static ApiResponse<T> Fail(string message) => new(false, message, default, new List<string> { message });
}

public sealed record FileData(
    [property: JsonPropertyName("FileId")] Guid FileId,
    [property: JsonPropertyName("FilePath")] string FilePath,
    [property: JsonPropertyName("Hash")] string? Hash,
    [property: JsonPropertyName("FileName")] string? FileName,
    [property: JsonPropertyName("MediaType")] string? MediaType,
    [property: JsonPropertyName("File")] string? File);

/// <summary>
/// The upload contract. There is no path field — the vendor assigns FileId, the yyyyMMdd
/// folder and the file name. Category is the only lever we have (spec section 3.2).
/// ApplicationId is sent as Guid.Empty to match current production behaviour, where
/// DocumentDataService never sets it.
/// </summary>
public sealed record UploadRequest(
    [property: JsonPropertyName("Category")] string Category,
    [property: JsonPropertyName("FileName")] string FileName,
    [property: JsonPropertyName("File")] string File,
    [property: JsonPropertyName("MediaType")] string MediaType,
    [property: JsonPropertyName("Extension")] string Extension,
    [property: JsonPropertyName("ApplicationId")] Guid ApplicationId);
