using System.IO;
using System.Threading.Tasks;
using Cafs.Core.Models;
using Xunit;

namespace Cafs.Core.Tests.Models;

/// <summary>
/// HydratedContent の責務:
///   - Stream + サーバから受け取った FileAttributes を保持して呼び出し側に渡す。
///   - IsReadOnly は ReadOnly フラグ有無を bool で露出する。
///   - Dispose / DisposeAsync で内側 Stream を確実に閉じる (ハイドレート後のリーク防止)。
/// </summary>
public class HydratedContentTests
{
    private sealed class TrackingStream : MemoryStream
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void Properties_ExposeStreamAndAttributes()
    {
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        var content = new HydratedContent(stream, FileAttributes.ReadOnly | FileAttributes.Hidden);

        Assert.Same(stream, content.Content);
        Assert.True(content.Attributes.HasFlag(FileAttributes.ReadOnly));
        Assert.True(content.Attributes.HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void IsReadOnly_TrueWhenReadOnlyFlagPresent()
    {
        using var stream = new MemoryStream();
        var withRo = new HydratedContent(stream, FileAttributes.ReadOnly);
        var withoutRo = new HydratedContent(stream, FileAttributes.Hidden);
        var none = new HydratedContent(stream, 0);

        Assert.True(withRo.IsReadOnly);
        Assert.False(withoutRo.IsReadOnly);
        Assert.False(none.IsReadOnly);
    }

    [Fact]
    public void Dispose_DisposesInnerStream()
    {
        var stream = new TrackingStream();
        var content = new HydratedContent(stream, 0);

        content.Dispose();

        Assert.True(stream.Disposed, "内側 Stream は Dispose で閉じられるべき");
    }

    [Fact]
    public async Task DisposeAsync_DisposesInnerStream()
    {
        var stream = new TrackingStream();
        var content = new HydratedContent(stream, 0);

        await content.DisposeAsync();

        Assert.True(stream.Disposed);
    }
}
