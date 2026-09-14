using MocdDocFix.Clients;

namespace MocdDocFix.Tests.Fakes;

public sealed class FakeFileServiceClient : IFileServiceClient
{
    /// <summary>path → (base64 content, vendor hash).</summary>
    public Dictionary<string, (string Base64, string Hash)> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<UploadRequest> Uploads { get; } = new();
    public List<string> Deleted { get; } = new();

    /// <summary>Set to control what the next upload returns.</summary>
    public Func<UploadRequest, ApiResponse<FileData>>? UploadResponder { get; set; }

    /// <summary>Paths the server cannot be reached about at all — the connection drops.</summary>
    public Dictionary<string, string> DownloadThrows { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<ApiResponse<FileData>> DownloadAsync(string filePath, CancellationToken ct)
    {
        if (DownloadThrows.TryGetValue(filePath, out var problem))
            throw new HttpRequestException(problem);

        return Task.FromResult(Files.TryGetValue(filePath, out var f)
            ? new ApiResponse<FileData>(true, null,
                new FileData(Guid.Empty, filePath, f.Hash, null, null, f.Base64), null)
            : ApiResponse<FileData>.Fail($"not found: {filePath}"));
    }

    public Task<ApiResponse<FileData>> UploadAsync(UploadRequest request, CancellationToken ct)
    {
        Uploads.Add(request);
        return Task.FromResult(UploadResponder?.Invoke(request)
            ?? ApiResponse<FileData>.Fail("no upload responder configured"));
    }

    /// <summary>
    /// When set, the delete reports success but the file is left in place — the way a server
    /// that lies about deleting would behave.
    /// </summary>
    public bool PretendToDelete { get; set; }

    /// <summary>When set, the delete call itself fails.</summary>
    public string? DeleteRefusal { get; set; }

    public Task<ApiResponse<bool>> DeleteAsync(string filePath, CancellationToken ct)
    {
        Deleted.Add(filePath);

        if (DeleteRefusal is not null)
            return Task.FromResult(ApiResponse<bool>.Fail(DeleteRefusal));

        if (!PretendToDelete) Files.Remove(filePath);
        return Task.FromResult(new ApiResponse<bool>(true, null, true, null));
    }
}
