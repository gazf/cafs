using System.Diagnostics;
using System.Runtime.InteropServices;
using CfApi.Native;

namespace CfApi.Interop;

/// <summary>
/// CfOpenFileWithOplock で開いたファイルの protected handle と、対応する Win32 HANDLE を保持する。
/// 書き込み系 CfApi 操作 (UpdatePlaceholder / SetInSyncState / ConvertToPlaceholder) を
/// インスタンスメソッドとして公開する。
///
/// Dispose で CfCloseHandle が呼ばれる。
/// </summary>
public sealed class OplockFileHandle : IDisposable
{
    private IntPtr _protectedHandle;

    /// <summary>protected handle. 参照カウント系 API はこの値を受け取る。</summary>
    public IntPtr ProtectedHandle => _protectedHandle;

    /// <summary>対応する Win32 HANDLE。書き込み系 CfApi (HANDLE を受ける API) に渡す。</summary>
    public IntPtr Win32Handle { get; }

    private OplockFileHandle(IntPtr protectedHandle, IntPtr win32Handle)
    {
        _protectedHandle = protectedHandle;
        Win32Handle = win32Handle;
    }

    public static OplockFileHandle Open(string filePath, OplockOpenFlags flags = OplockOpenFlags.None)
    {
        var hr = CldApi.CfOpenFileWithOplock(filePath, (CF_OPEN_FILE_FLAGS)flags, out var protectedHandle);
        if (CldApi.Failed(hr))
            throw new InvalidOperationException($"CfOpenFileWithOplock('{filePath}') failed: 0x{hr:X8}");

        var win32 = CldApi.CfGetWin32HandleFromProtectedHandle(protectedHandle);
        return new OplockFileHandle(protectedHandle, win32);
    }

    public void SetInSyncState(bool inSync)
    {
        var state = inSync
            ? CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC
            : CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_NOT_IN_SYNC;

        int hr;
        unsafe
        {
            hr = CldApi.CfSetInSyncState(Win32Handle, state, CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE, null);
        }
        if (CldApi.Failed(hr))
            throw new InvalidOperationException($"CfSetInSyncState failed: 0x{hr:X8}");
    }

    /// <summary>USN チェック付きで in-sync state を設定。USN が一致しない場合は失敗する。</summary>
    public void SetInSyncState(bool inSync, ref long inSyncUsn)
    {
        var state = inSync
            ? CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC
            : CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_NOT_IN_SYNC;

        int hr;
        unsafe
        {
            fixed (long* pUsn = &inSyncUsn)
            {
                hr = CldApi.CfSetInSyncState(Win32Handle, state, CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE, pUsn);
            }
        }
        if (CldApi.Failed(hr))
            throw new InvalidOperationException($"CfSetInSyncState failed: 0x{hr:X8}");
    }

    /// <summary>
    /// placeholder のメタデータ・FileIdentity を更新する。どちらも optional。
    /// dehydrateRanges を指定すると該当範囲を dehydrate する。
    /// </summary>
    public void UpdatePlaceholder(
        FileMetadata? metadata,
        ReadOnlySpan<byte> fileIdentity,
        ReadOnlySpan<FileRange> dehydrateRanges,
        UpdateFlags flags)
    {
        int hr;
        unsafe
        {
            CF_FS_METADATA nativeMeta = default;
            CF_FS_METADATA* pMeta = null;
            if (metadata.HasValue)
            {
                nativeMeta = ToNative(metadata.Value);
                pMeta = &nativeMeta;
            }

            // FileRange / CF_FILE_RANGE は同一レイアウト (long, long) なのでそのままキャスト。
            var nativeRanges = MemoryMarshal.Cast<FileRange, CF_FILE_RANGE>(dehydrateRanges);

            fixed (byte* pIdentity = fileIdentity)
            fixed (CF_FILE_RANGE* pRanges = nativeRanges)
            {
                hr = CldApi.CfUpdatePlaceholder(
                    Win32Handle,
                    pMeta,
                    pIdentity,
                    (uint)fileIdentity.Length,
                    pRanges,
                    (uint)nativeRanges.Length,
                    (CF_UPDATE_FLAGS)flags,
                    null,
                    null);
            }
        }
        if (CldApi.Failed(hr))
            throw new InvalidOperationException($"CfUpdatePlaceholder failed: 0x{hr:X8}");
    }

    public void ConvertToPlaceholder(ReadOnlySpan<byte> fileIdentity, ConvertFlags flags = ConvertFlags.None)
    {
        int hr;
        unsafe
        {
            fixed (byte* pIdentity = fileIdentity)
            {
                hr = CldApi.CfConvertToPlaceholder(
                    Win32Handle,
                    pIdentity,
                    (uint)fileIdentity.Length,
                    (CF_CONVERT_FLAGS)flags,
                    null,
                    null);
            }
        }
        if (CldApi.Failed(hr))
            throw new InvalidOperationException($"CfConvertToPlaceholder failed: 0x{hr:X8}");
    }

    /// <summary>CfReferenceProtectedHandle — 成功すると true を返す。</summary>
    public bool Reference() => CldApi.CfReferenceProtectedHandle(_protectedHandle);

    /// <summary>CfReleaseProtectedHandle — Reference と対で呼ぶ。</summary>
    public void Release() => CldApi.CfReleaseProtectedHandle(_protectedHandle);

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _protectedHandle, IntPtr.Zero);
        if (handle == IntPtr.Zero) return;
        var hr = CldApi.CfCloseHandle(handle);
        if (CldApi.Failed(hr))
            Trace.WriteLine($"CfCloseHandle failed: 0x{hr:X8}");
    }

    private static CF_FS_METADATA ToNative(FileMetadata m) => new()
    {
        FileSize = m.FileSize,
        BasicInfo = new FILE_BASIC_INFO
        {
            CreationTime = m.Creation.ToFileTimeUtc(),
            LastAccessTime = m.LastAccess.ToFileTimeUtc(),
            LastWriteTime = m.LastWrite.ToFileTimeUtc(),
            ChangeTime = m.Change.ToFileTimeUtc(),
            FileAttributes = m.FileAttributes,
        },
    };
}
