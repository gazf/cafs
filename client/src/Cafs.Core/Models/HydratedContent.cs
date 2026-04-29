namespace Cafs.Core.Models;

/// <summary>
/// ADR-019: GET /content/* の戻り値。本体ストリームとサーバが伝達したファイル属性
/// (ReadOnly 等) を一括で持つ。Dispose で内側のストリームも閉じる。
/// </summary>
public sealed class HydratedContent : IAsyncDisposable, IDisposable
{
    public Stream Content { get; }
    public FileAttributes Attributes { get; }

    public HydratedContent(Stream content, FileAttributes attributes)
    {
        Content = content;
        Attributes = attributes;
    }

    public bool IsReadOnly => (Attributes & FileAttributes.ReadOnly) != 0;

    public ValueTask DisposeAsync() => Content is IAsyncDisposable a
        ? a.DisposeAsync()
        : new ValueTask(Task.Run(Content.Dispose));

    public void Dispose() => Content.Dispose();
}
