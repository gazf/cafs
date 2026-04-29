using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cafs.Core.Abstractions;
using Cafs.Core.Sync;
using CfApi.Interop;
using Moq;
using Xunit;

namespace Cafs.Core.Tests.Sync;

/// <summary>
/// SyncEngine の責務 (CfApi 不要部分):
///   - WSS から流れてきた lock_acquired / lock_released を受信し、当該パスの
///     ローカル NTFS の ReadOnly 属性を holder の device 次第でトグルする (ADR-019)。
///   - 自端末がホルダーなら属性を変更しない (自分の編集を RO にしてしまわない)。
///   - holder が null のメッセージは無視する。
///
/// FullSync / created / modified / deleted は SyncProvider.CreatePlaceholders 等
/// CfApi 経由の Win32 を呼ぶため、ここでは触らず実機検証に委ねる。
/// </summary>
public class SyncEngineTests : IDisposable
{
    private const string SelfDeviceId = "dev-self-12345678";
    private const string OtherDeviceId = "dev-other-87654321";

    private readonly string _syncRoot;

    public SyncEngineTests()
    {
        _syncRoot = Path.Combine(Path.GetTempPath(), "cafs-syncengine-" + Guid.NewGuid());
        Directory.CreateDirectory(_syncRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_syncRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// 単発イベントを 1 回だけ yield して完了する fake IEventStream。
    /// SyncEngine.RunEventLoopAsync が HandleEvent を呼ぶことを観測するために使う。
    /// </summary>
    private sealed class SingleEventStream : IEventStream
    {
        private readonly ServerEvent _event;
        public SingleEventStream(ServerEvent evt) => _event = evt;

#pragma warning disable CS1998 // async without await — IAsyncEnumerable 仕様上必要
        public async IAsyncEnumerable<ServerEvent> ReadEventsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
#pragma warning restore CS1998
        {
            yield return _event;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private SyncEngine NewEngine(out string filePath)
    {
        // SyncEngine 構築には SyncProvider が必要 (lock イベント処理経路では呼ばれないが
        // 引数として要る)。Connect() しなければ Win32 には触らない前提。
        var server = new Mock<ICafsServer>(MockBehavior.Strict).Object;
        var callbacks = new Mock<ISyncCallbacks>().Object;
        var provider = new SyncProvider(_syncRoot, callbacks);

        var engine = new SyncEngine(server, provider, _syncRoot, SelfDeviceId);

        filePath = Path.Combine(_syncRoot, "foo.txt");
        File.WriteAllText(filePath, "x");
        return engine;
    }

    private static ServerEvent LockEvent(string type, string path, string holderDeviceId, int holderUserId = 99)
        => new(
            Event: type,
            Path: path,
            Holder: new LockHolder(holderUserId, holderDeviceId, "someone"));

    [Fact]
    public async Task HandleEvent_LockAcquiredByOtherDevice_SetsReadOnly()
    {
        var engine = NewEngine(out var filePath);
        Assert.False(File.GetAttributes(filePath).HasFlag(FileAttributes.ReadOnly));

        var stream = new SingleEventStream(LockEvent("lock_acquired", "/foo.txt", OtherDeviceId));
        await engine.RunEventLoopAsync(stream, CancellationToken.None);

        Assert.True(File.GetAttributes(filePath).HasFlag(FileAttributes.ReadOnly),
            "他 device の lock_acquired を受けたら RO になるべき");
    }

    [Fact]
    public async Task HandleEvent_LockReleasedByOtherDevice_ClearsReadOnly()
    {
        var engine = NewEngine(out var filePath);
        File.SetAttributes(filePath, File.GetAttributes(filePath) | FileAttributes.ReadOnly);

        var stream = new SingleEventStream(LockEvent("lock_released", "/foo.txt", OtherDeviceId));
        await engine.RunEventLoopAsync(stream, CancellationToken.None);

        Assert.False(File.GetAttributes(filePath).HasFlag(FileAttributes.ReadOnly),
            "他 device の lock_released を受けたら RO は外れるべき");
    }

    [Fact]
    public async Task HandleEvent_LockAcquiredByOwnDevice_DoesNotChangeAttributes()
    {
        // 自端末 = ホルダーの場合、ローカルの編集を RO にしてはいけない (ADR-019)
        var engine = NewEngine(out var filePath);
        var initial = File.GetAttributes(filePath);

        var stream = new SingleEventStream(LockEvent("lock_acquired", "/foo.txt", SelfDeviceId));
        await engine.RunEventLoopAsync(stream, CancellationToken.None);

        Assert.Equal(initial, File.GetAttributes(filePath));
    }

    [Fact]
    public async Task HandleEvent_LockReleasedByOwnDevice_DoesNotChangeAttributes()
    {
        // 自端末の release は no-op (自分の解放で自分のローカルを変えない)
        var engine = NewEngine(out var filePath);
        File.SetAttributes(filePath, File.GetAttributes(filePath) | FileAttributes.ReadOnly);
        var initial = File.GetAttributes(filePath);

        var stream = new SingleEventStream(LockEvent("lock_released", "/foo.txt", SelfDeviceId));
        await engine.RunEventLoopAsync(stream, CancellationToken.None);

        Assert.Equal(initial, File.GetAttributes(filePath));
    }

    [Fact]
    public async Task HandleEvent_LockEventWithoutHolder_IsIgnored()
    {
        var engine = NewEngine(out var filePath);
        var initial = File.GetAttributes(filePath);

        var stream = new SingleEventStream(new ServerEvent(
            Event: "lock_acquired",
            Path: "/foo.txt",
            Holder: null));
        await engine.RunEventLoopAsync(stream, CancellationToken.None);

        Assert.Equal(initial, File.GetAttributes(filePath));
    }

    [Fact]
    public async Task HandleEvent_LockEventForMissingFile_IsNoOp()
    {
        // ローカルに存在しないファイルへの lock イベント = LocalAttributes が
        // 例外を出さずに無視する (ADR-019)
        var engine = NewEngine(out _);
        var stream = new SingleEventStream(LockEvent("lock_acquired", "/no-such-file.txt", OtherDeviceId));

        await engine.RunEventLoopAsync(stream, CancellationToken.None); // 例外が出ないこと
    }
}
