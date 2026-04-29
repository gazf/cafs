using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Cafs.Core.Abstractions;
using Cafs.Core.Models;
using Cafs.Core.Sync;
using Moq;
using Xunit;

namespace Cafs.Core.Tests.Sync;

/// <summary>
/// CafsSyncCallbacks の責務 (CfApi 不要部分):
///   - OnFileOpenAsync (ADR-016): open 時に AcquireLock を呼び、成功時はローカルの
///     RO 属性を外す (WSS 取りこぼし救済)。失敗・例外でも open をブロックしない。
///   - OnDeleteAsync: server.DeleteFileAsync に伝播。失敗時は STATUS_ACCESS_DENIED 返却。
///   - OnFileCloseAsync (read-only path): isModified=false なら ReleaseLock のみで終了。
///   - OnFileCloseAsync (conflict file path): AcquireLock 失敗時、ローカルファイルを
///     "<stem>.conflict-<timestamp><ext>" にコピーし、true を返して dehydrate を促す。
///
/// HydrateAsync と書き戻しの upload 経路は OplockFileHandle / DataTransfer (Win32) を
/// 触るため、ここでは扱わず実機検証に委ねる (ADR-011)。
/// </summary>
public class CafsSyncCallbacksTests : IDisposable
{
    private readonly string _syncRoot;

    public CafsSyncCallbacksTests()
    {
        _syncRoot = Path.Combine(Path.GetTempPath(), "cafs-callbacks-" + Guid.NewGuid());
        Directory.CreateDirectory(_syncRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_syncRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    private string TouchLocal(string relPath, string contents = "x", FileAttributes attrs = 0)
    {
        var local = Path.Combine(_syncRoot, relPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.WriteAllText(local, contents);
        if (attrs != 0)
            File.SetAttributes(local, File.GetAttributes(local) | attrs);
        return local;
    }

    private static LockInfo MakeLock() => new(
        UserId: 1,
        AcquiredAt: DateTime.UtcNow.ToString("o"),
        ExpiresAt: DateTime.UtcNow.AddSeconds(30).ToString("o"));

    // ---------- OnFileOpenAsync ----------

    [Fact]
    public async Task OnFileOpenAsync_AcquireSucceeds_ClearsLocalReadOnly()
    {
        // 事前条件: 他端末ロック由来で RO が立っている状態
        var local = TouchLocal("/foo.txt", attrs: FileAttributes.ReadOnly);
        Assert.True(File.GetAttributes(local).HasFlag(FileAttributes.ReadOnly));

        var server = new Mock<ICafsServer>(MockBehavior.Strict);
        server.Setup(s => s.AcquireLockAsync("/foo.txt", It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeLock());

        var sut = new CafsSyncCallbacks(server.Object, _syncRoot);
        await sut.OnFileOpenAsync("/foo.txt", CancellationToken.None);

        Assert.False(File.GetAttributes(local).HasFlag(FileAttributes.ReadOnly),
            "自端末がロックを取得したので RO は外れるべき");
        server.VerifyAll();
    }

    [Fact]
    public async Task OnFileOpenAsync_AcquireDenied_DoesNotChangeAttributes()
    {
        var local = TouchLocal("/foo.txt", attrs: FileAttributes.ReadOnly);

        var server = new Mock<ICafsServer>();
        server.Setup(s => s.AcquireLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((LockInfo?)null); // 他ユーザー保持中

        var sut = new CafsSyncCallbacks(server.Object, _syncRoot);
        await sut.OnFileOpenAsync("/foo.txt", CancellationToken.None);

        Assert.True(File.GetAttributes(local).HasFlag(FileAttributes.ReadOnly),
            "拒否されたので RO はそのまま維持されるべき");
    }

    [Fact]
    public async Task OnFileOpenAsync_ServerThrows_DoesNotPropagate()
    {
        TouchLocal("/foo.txt");

        var server = new Mock<ICafsServer>();
        server.Setup(s => s.AcquireLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new HttpRequestException("network down"));

        var sut = new CafsSyncCallbacks(server.Object, _syncRoot);
        // 例外が漏れないこと = open がブロックされない (ADR-016 best-effort)
        await sut.OnFileOpenAsync("/foo.txt", CancellationToken.None);
    }

    // ---------- OnDeleteAsync ----------

    [Fact]
    public async Task OnDeleteAsync_Success_ReturnsZero()
    {
        var server = new Mock<ICafsServer>();
        server.Setup(s => s.DeleteFileAsync("/x.txt", It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        var sut = new CafsSyncCallbacks(server.Object, _syncRoot);
        var status = await sut.OnDeleteAsync("/x.txt", CancellationToken.None);
        Assert.Equal(0, status);
    }

    [Fact]
    public async Task OnDeleteAsync_ServerThrows_ReturnsStatusAccessDenied()
    {
        const int StatusAccessDenied = unchecked((int)0xC0000022);
        var server = new Mock<ICafsServer>();
        server.Setup(s => s.DeleteFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new HttpRequestException("server down"));

        var sut = new CafsSyncCallbacks(server.Object, _syncRoot);
        var status = await sut.OnDeleteAsync("/x.txt", CancellationToken.None);
        Assert.Equal(StatusAccessDenied, status);
    }

    // ---------- OnFileCloseAsync (read-only path) ----------

    [Fact]
    public async Task OnFileCloseAsync_ReadOnlyClose_ReleasesLockAndReturnsTrue()
    {
        TouchLocal("/foo.txt");
        var server = new Mock<ICafsServer>();
        server.Setup(s => s.ReleaseLockAsync("/foo.txt", It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        var sut = new CafsSyncCallbacks(server.Object, _syncRoot);
        var ok = await sut.OnFileCloseAsync("/foo.txt", isDeleted: false, isModified: false, CancellationToken.None);

        Assert.True(ok);
        server.Verify(s => s.ReleaseLockAsync("/foo.txt", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnFileCloseAsync_ReadOnlyClose_ReleaseFailureSwallowed()
    {
        TouchLocal("/foo.txt");
        var server = new Mock<ICafsServer>();
        server.Setup(s => s.ReleaseLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new HttpRequestException("transient"));

        var sut = new CafsSyncCallbacks(server.Object, _syncRoot);
        // 例外を握り潰し、true (= dehydrate OK) を返す
        var ok = await sut.OnFileCloseAsync("/foo.txt", isDeleted: false, isModified: false, CancellationToken.None);
        Assert.True(ok);
    }

    [Fact]
    public async Task OnFileCloseAsync_DeletedFlag_ReturnsTrueWithoutTouchingServer()
    {
        var server = new Mock<ICafsServer>(MockBehavior.Strict);
        var sut = new CafsSyncCallbacks(server.Object, _syncRoot);

        var ok = await sut.OnFileCloseAsync("/gone.txt", isDeleted: true, isModified: true, CancellationToken.None);
        Assert.True(ok);
        // Strict + Setup なし → 何も呼ばれていないこと
    }

    // ---------- OnFileCloseAsync (conflict file path) ----------

    [Fact]
    public async Task OnFileCloseAsync_LockDenied_SavesConflictFileAndReturnsTrue()
    {
        // modified だが他ユーザーが保持中 → conflict file 退避 (ADR-017)
        var local = TouchLocal("/notes.txt", contents: "my edits");
        var dir = Path.GetDirectoryName(local)!;

        var server = new Mock<ICafsServer>();
        server.Setup(s => s.AcquireLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((LockInfo?)null);

        var sut = new CafsSyncCallbacks(server.Object, _syncRoot);
        var ok = await sut.OnFileCloseAsync("/notes.txt", isDeleted: false, isModified: true, CancellationToken.None);

        Assert.True(ok); // 元ファイルは dehydrate (= サーバ版に戻す) させる

        // 同ディレクトリに <stem>.conflict-<yyyyMMdd-HHmmss><ext> が出来ていること
        var conflicts = Directory.GetFiles(dir, "notes.conflict-*.txt");
        Assert.NotEmpty(conflicts);
        var content = File.ReadAllText(conflicts.First());
        Assert.Equal("my edits", content); // ローカル変更が中身として保存されている
    }

    [Fact]
    public async Task OnFileCloseAsync_LockDenied_NoLocalFile_StillReturnsTrue()
    {
        // ローカルに実体が無いケース (rare): conflict file を作る対象が無いだけで true を返す
        var server = new Mock<ICafsServer>();
        server.Setup(s => s.AcquireLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((LockInfo?)null);

        var sut = new CafsSyncCallbacks(server.Object, _syncRoot);
        var ok = await sut.OnFileCloseAsync("/missing.txt", isDeleted: false, isModified: true, CancellationToken.None);
        Assert.True(ok);
    }
}
