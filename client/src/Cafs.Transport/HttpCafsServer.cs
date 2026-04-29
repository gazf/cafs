using System.Net.Http.Headers;
using System.Text.Json;
using Cafs.Core.Abstractions;
using Cafs.Core.Models;

namespace Cafs.Transport;

public class CafsApiException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public class HttpCafsServer(HttpClient http, string baseUrl) : ICafsServer, IDisposable
{
    private readonly HttpClient _http = http;
    private readonly string _baseUrl = baseUrl.TrimEnd('/');

    public async Task<IReadOnlyList<TreeNode>> GetTreeAsync(CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/tree";
        var response = await _http.GetAsync(url, ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<TreeNode>>(json) ?? [];
    }

    public async Task<IReadOnlyList<FileNode>> ListDirectoryAsync(string path, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/files{NormalizePath(path)}";
        var response = await _http.GetAsync(url, ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<FileNode>>(json) ?? [];
    }

    public async Task<FileNode> GetFileInfoAsync(string path, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/files{NormalizePath(path)}";
        var response = await _http.GetAsync(url, ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<FileNode>(json)
            ?? throw new CafsApiException("Failed to parse response", 500);
    }

    public async Task<HydratedContent> DownloadFileAsync(string path, long offset = 0, long length = -1, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/content{NormalizePath(path)}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (offset > 0 || length > 0)
        {
            var end = length > 0 ? offset + length - 1 : (long?)null;
            request.Headers.Range = new RangeHeaderValue(offset, end);
        }

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);

        var attributes = ParseFileAttributes(response);
        var stream = await response.Content.ReadAsStreamAsync(ct);
        return new HydratedContent(stream, attributes);
    }

    private static FileAttributes ParseFileAttributes(HttpResponseMessage response)
    {
        // ADR-019: X-File-Attributes はカンマ区切り (ReadOnly, Hidden, ...)。
        // 未知の値は無視。空 / 欠如時は属性なし (0)。
        if (!response.Headers.TryGetValues("X-File-Attributes", out var values))
            return 0;

        FileAttributes result = 0;
        foreach (var raw in values)
        {
            foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Enum.TryParse<FileAttributes>(token, ignoreCase: true, out var attr))
                    result |= attr;
            }
        }
        return result;
    }

    public async Task<UploadResult> UploadFileAsync(string path, Stream content, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/content{NormalizePath(path)}";
        using var streamContent = new StreamContent(content);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var response = await _http.PutAsync(url, streamContent, ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<UploadResult>(json)
            ?? throw new CafsApiException("Failed to parse upload response", 500);
    }

    public async Task DeleteFileAsync(string path, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/files{NormalizePath(path)}";
        var response = await _http.DeleteAsync(url, ct);
        if ((int)response.StatusCode == 404) return;
        await EnsureSuccess(response, ct);
    }

    public async Task<LockInfo?> AcquireLockAsync(string path, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/locks{NormalizePath(path)}";
        var response = await _http.PostAsync(url, null, ct);
        // 他ユーザーが保持中は HTTP 409 → 例外ではなく null で返す (呼び出し側でハンドル)
        if ((int)response.StatusCode == 409) return null;
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<LockInfo>(json);
    }

    public async Task ReleaseLockAsync(string path, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/locks{NormalizePath(path)}";
        var response = await _http.DeleteAsync(url, ct);
        await EnsureSuccess(response, ct);
    }

    private static string NormalizePath(string path)
    {
        path = path.Replace('\\', '/');
        if (!path.StartsWith('/'))
            path = "/" + path;
        return path;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct = default)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            string message;
            try
            {
                var error = JsonSerializer.Deserialize<ErrorResponse>(body);
                message = error?.Message ?? body;
            }
            catch
            {
                message = body;
            }
            throw new CafsApiException(message, (int)response.StatusCode);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
