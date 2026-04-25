using System.Net.Http.Headers;
using System.Text.Json;
using Cafs.Core.Abstractions;
using Cafs.Core.Models;

namespace Cafs.Transport;

public class CafsApiException : Exception
{
    public int StatusCode { get; }

    public CafsApiException(string message, int statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }
}

public class HttpCafsServer : ICafsServer, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public HttpCafsServer(string baseUrl, string bearerToken)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    public async Task<IReadOnlyList<FileNode>> ListDirectoryAsync(string path)
    {
        var url = $"{_baseUrl}/files{NormalizePath(path)}";
        var response = await _http.GetAsync(url);
        await EnsureSuccess(response);
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<FileNode>>(json) ?? [];
    }

    public async Task<FileNode> GetFileInfoAsync(string path)
    {
        var url = $"{_baseUrl}/files{NormalizePath(path)}";
        var response = await _http.GetAsync(url);
        await EnsureSuccess(response);
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<FileNode>(json)
            ?? throw new CafsApiException("Failed to parse response", 500);
    }

    public async Task<Stream> DownloadFileAsync(string path, long offset = 0, long length = -1)
    {
        var url = $"{_baseUrl}/content{NormalizePath(path)}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (offset > 0 || length > 0)
        {
            var end = length > 0 ? offset + length - 1 : (long?)null;
            request.Headers.Range = new RangeHeaderValue(offset, end);
        }

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await EnsureSuccess(response);
        return await response.Content.ReadAsStreamAsync();
    }

    public async Task UploadFileAsync(string path, Stream content)
    {
        var url = $"{_baseUrl}/content{NormalizePath(path)}";
        using var streamContent = new StreamContent(content);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var response = await _http.PutAsync(url, streamContent);
        await EnsureSuccess(response);
    }

    public async Task DeleteFileAsync(string path)
    {
        var url = $"{_baseUrl}/files{NormalizePath(path)}";
        var response = await _http.DeleteAsync(url);
        if ((int)response.StatusCode == 404) return;
        await EnsureSuccess(response);
    }

    public async Task<LockInfo?> AcquireLockAsync(string path)
    {
        var url = $"{_baseUrl}/locks{NormalizePath(path)}";
        var response = await _http.PostAsync(url, null);
        await EnsureSuccess(response);
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<LockInfo>(json);
    }

    public async Task ReleaseLockAsync(string path)
    {
        var url = $"{_baseUrl}/locks{NormalizePath(path)}";
        var response = await _http.DeleteAsync(url);
        await EnsureSuccess(response);
    }

    private static string NormalizePath(string path)
    {
        path = path.Replace('\\', '/');
        if (!path.StartsWith('/'))
            path = "/" + path;
        return path;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
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
