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

    public Task<ApiResponse<FileData>> DownloadAsync(string filePath, CancellationToken ct) =>
        Task.FromResult(Files.TryGetValue(filePath, out var f)
            ? new ApiResponse<FileData>(true, null,
                new FileData(Guid.Empty, filePath, f.Hash, null, null, f.Base64), null)
            : ApiResponse<FileData>.Fail($"not found: {filePath}"));

    public Task<ApiResponse<FileData>> UploadAsync(UploadRequest request, CancellationToken ct)
    {
        Uploads.Add(request);
        return Task.FromResult(UploadResponder?.Invoke(request)
            ?? ApiResponse<FileData>.Fail("no upload responder configured"));
    }

    public Task<ApiResponse<bool>> DeleteAsync(string filePath, CancellationToken ct)
    {
        Deleted.Add(filePath);
        Files.Remove(filePath);
        return Task.FromResult(new ApiResponse<bool>(true, null, true, null));
    }
}
