using System.Buffers;
using CfApi.Native;

namespace CfApi.Interop.Internal;

internal static class Marshaller
{
    // 変換後は先頭 '/' を追加する分最大 +1 なので、入力が 512 以内なら stackalloc 520 で収まる。
    private const int StackallocInputLimit = 512;
    private const int StackallocBufferSize = StackallocInputLimit + 8;

    /// <summary>
    /// NormalizedPath ("\SyncRoot\sub\file" — volume-relative) から syncRoot を剥いで "/sub/file" 形式にする。
    /// prefix 除去と '\\' → '/' 変換を 1 パスで実施し、終段で string を 1 回だけ生成する。
    /// </summary>
    public static unsafe string GetRelativePath(CF_CALLBACK_INFO* info, string syncRootPath)
    {
        if (info->NormalizedPath is null)
            return "/";

        int srcLen = 0;
        while (info->NormalizedPath[srcLen] != '\0') srcLen++;

        var source = new ReadOnlySpan<char>(info->NormalizedPath, srcLen);

        char[]? rented = null;
        Span<char> buffer = srcLen <= StackallocInputLimit
            ? stackalloc char[StackallocBufferSize]
            : (rented = ArrayPool<char>.Shared.Rent(srcLen + 8));

        try
        {
            var sliced = StripPrefix(source, syncRootPath);

            int written = 0;
            bool leading = true;
            foreach (var ch in sliced)
            {
                var c = ch == '\\' ? '/' : ch;
                if (leading && c == '/')
                {
                    buffer[written++] = '/';
                    leading = false;
                    continue;
                }
                if (leading)
                {
                    buffer[written++] = '/';
                    leading = false;
                }
                buffer[written++] = c;
            }

            if (written == 0) return "/";
            return new string(buffer[..written]);
        }
        finally
        {
            if (rented is not null) ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static ReadOnlySpan<char> StripPrefix(ReadOnlySpan<char> source, string syncRootPath)
    {
        // フルパス形式 ("C:\CAFS\sub") → そのまま一致
        if (source.StartsWith(syncRootPath, StringComparison.OrdinalIgnoreCase))
            return source[syncRootPath.Length..];

        // NormalizedPath はボリューム相対形式 ("\CAFS\sub") のため、
        // syncRootPath のドライブ文字部分 ("C:") を除いた部分で再試行する。
        int driveEnd = syncRootPath.IndexOf(':');
        if (driveEnd >= 0)
        {
            var syncRelative = syncRootPath.AsSpan(driveEnd + 1); // e.g. "\CAFS"
            if (source.StartsWith(syncRelative, StringComparison.OrdinalIgnoreCase))
                return source[syncRelative.Length..];
        }

        return source;
    }

    /// <summary>
    /// PlaceholderInfo 列から CF_PLACEHOLDER_CREATE_INFO 配列を組み立てる。
    /// 返り値は ArrayPool から借りたバッファの所有権を持つ。Dispose 必須。
    /// </summary>
    public static PlaceholderBatch BuildPlaceholders(IReadOnlyList<PlaceholderInfo> entries)
    {
        return PlaceholderBatch.Build(entries);
    }
}
