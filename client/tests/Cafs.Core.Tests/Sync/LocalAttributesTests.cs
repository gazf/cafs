using System;
using System.IO;
using Cafs.Core.Sync;
using Xunit;

namespace Cafs.Core.Tests.Sync;

/// <summary>
/// LocalAttributes.SetReadOnly の責務:
///   - true を渡すと ReadOnly 属性を立てる。
///   - false を渡すと ReadOnly 属性を下ろす。
///   - 他の属性 (Hidden 等) は維持する。
///   - 存在しないファイルでは no-op (例外を投げない)。
/// </summary>
public class LocalAttributesTests : IDisposable
{
    private readonly string _temp;

    public LocalAttributesTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "cafs-localattr-" + Guid.NewGuid());
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch { /* best-effort */ }
    }

    private string CreateFile(string name = "f.txt")
    {
        var path = Path.Combine(_temp, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void SetReadOnly_True_AddsReadOnlyFlag()
    {
        var path = CreateFile();
        Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));

        LocalAttributes.SetReadOnly(path, readOnly: true);

        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void SetReadOnly_False_RemovesReadOnlyFlag()
    {
        var path = CreateFile();
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        LocalAttributes.SetReadOnly(path, readOnly: false);

        Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void SetReadOnly_PreservesOtherAttributes()
    {
        // Hidden + ReadOnly が立っている状態で ReadOnly だけ落とす。
        // Linux 上の .NET では File.SetAttributes(Hidden) が NTFS 相当に永続しないので
        // Windows 限定。Cafs.App は Windows 専用 (net10.0-windows) なので実害なし。
        if (!OperatingSystem.IsWindows()) return;

        var path = CreateFile();
        File.SetAttributes(path,
            File.GetAttributes(path) | FileAttributes.Hidden | FileAttributes.ReadOnly);

        LocalAttributes.SetReadOnly(path, readOnly: false);

        var attrs = File.GetAttributes(path);
        Assert.False(attrs.HasFlag(FileAttributes.ReadOnly), "ReadOnly は落ちる");
        Assert.True(attrs.HasFlag(FileAttributes.Hidden), "Hidden は維持されるべき");
    }

    [Fact]
    public void SetReadOnly_AlreadyDesiredState_NoChange()
    {
        // 既に ReadOnly が立っている時に true を渡しても問題なく完了する
        var path = CreateFile();
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        LocalAttributes.SetReadOnly(path, readOnly: true);

        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void SetReadOnly_NonExistentFile_DoesNotThrow()
    {
        var missing = Path.Combine(_temp, "nope.txt");
        // 例外が出ないこと、戻り値も void なので「throw しない」のみ確認
        LocalAttributes.SetReadOnly(missing, readOnly: true);
    }
}
