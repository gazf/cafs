using System.Diagnostics;

namespace Cafs.Core.Sync;

/// <summary>
/// ADR-019: サーバが伝達したファイル属性 (ReadOnly 等) を、ローカル NTFS の MFT に反映する。
/// hydrate 後 (X-File-Attributes) や WSS lock_acquired/released イベント受信時に呼ぶ。
/// </summary>
public static class LocalAttributes
{
    /// <summary>
    /// readOnly = true なら ReadOnly 属性を立てる、false なら下ろす。他の属性は維持。
    /// </summary>
    public static void SetReadOnly(string localPath, bool readOnly)
    {
        if (!File.Exists(localPath)) return;
        try
        {
            var current = File.GetAttributes(localPath);
            var updated = readOnly
                ? current | FileAttributes.ReadOnly
                : current & ~FileAttributes.ReadOnly;
            if (updated != current)
                File.SetAttributes(localPath, updated);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"SetReadOnly failed: {localPath}: {ex.Message}");
        }
    }
}
