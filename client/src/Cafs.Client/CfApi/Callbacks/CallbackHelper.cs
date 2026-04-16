using System.Runtime.InteropServices;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace Cafs.Client.CfApi.Callbacks;

internal static class CallbackHelper
{
    /// <summary>
    /// Extracts the relative server path from the CfApi callback's NormalizedPath.
    /// NormalizedPath is like "\syncRootPath\subdir\file.txt"
    /// We need to strip the sync root prefix to get "/subdir/file.txt"
    /// </summary>
    public static string GetRelativePath(in CF_CALLBACK_INFO callbackInfo, string syncRootPath)
    {
        var normalizedPath = callbackInfo.NormalizedPath ?? "";

        // Remove the sync root prefix
        if (normalizedPath.StartsWith(syncRootPath, StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = normalizedPath[syncRootPath.Length..];
        }

        // Convert backslashes to forward slashes
        normalizedPath = normalizedPath.Replace('\\', '/');

        if (string.IsNullOrEmpty(normalizedPath))
            normalizedPath = "/";

        if (!normalizedPath.StartsWith('/'))
            normalizedPath = "/" + normalizedPath;

        return normalizedPath;
    }
}
