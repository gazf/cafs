using System.Buffers;
using CfApi.Native;

namespace CfApi.Interop.Internal;

/// <summary>
/// CF_PLACEHOLDER_CREATE_INFO 配列とその name バッファの所有権を束ねる。
/// RelativeFileName / FileIdentity ポインタは Names 配列を pin した後で PatchPointers() 経由で設定する。
/// Dispose で ArrayPool に返却。
/// </summary>
internal ref struct PlaceholderBatch
{
    internal char[] Names;
    internal CF_PLACEHOLDER_CREATE_INFO[] Placeholders;
    internal int[] NameOffsets;      // Placeholders[i] の name が Names の何文字目から始まるか
    internal int[] NameByteLengths;  // null 終端込みの byte 長 (FileIdentityLength 用)
    internal int Count;

    public static PlaceholderBatch Build(IReadOnlyList<PlaceholderInfo> entries)
    {
        var count = entries.Count;

        int totalChars = 0;
        for (int i = 0; i < count; i++)
            totalChars += entries[i].Name.Length + 1;

        var names = ArrayPool<char>.Shared.Rent(totalChars);
        var placeholders = ArrayPool<CF_PLACEHOLDER_CREATE_INFO>.Shared.Rent(count);
        var offsets = ArrayPool<int>.Shared.Rent(count);
        var byteLens = ArrayPool<int>.Shared.Rent(count);

        int cursor = 0;
        for (int i = 0; i < count; i++)
        {
            var entry = entries[i];
            var nameLen = entry.Name.Length;
            var bufBytes = (nameLen + 1) * sizeof(char);

            offsets[i] = cursor;
            byteLens[i] = bufBytes;

            entry.Name.AsSpan().CopyTo(names.AsSpan(cursor, nameLen));
            names[cursor + nameLen] = '\0';
            cursor += nameLen + 1;

            var lastModified = entry.LastModified.ToFileTimeUtc();

            placeholders[i] = new CF_PLACEHOLDER_CREATE_INFO
            {
                RelativeFileName = null, // PatchPointers で埋める
                FsMetadata = new CF_FS_METADATA
                {
                    FileSize = entry.IsDirectory ? 0 : entry.Size,
                    BasicInfo = new FILE_BASIC_INFO
                    {
                        CreationTime = lastModified,
                        LastAccessTime = lastModified,
                        LastWriteTime = lastModified,
                        ChangeTime = lastModified,
                        FileAttributes = entry.IsDirectory ? 0x10u : 0x80u,
                    },
                },
                FileIdentity = null, // PatchPointers で埋める
                FileIdentityLength = (uint)bufBytes,
                Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC
                      | CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_SUPERSEDE,
                Result = 0,
                CreateUsn = 0,
            };
        }

        return new PlaceholderBatch
        {
            Names = names,
            Placeholders = placeholders,
            NameOffsets = offsets,
            NameByteLengths = byteLens,
            Count = count,
        };
    }

    /// <summary>
    /// Names 配列と Placeholders 配列が両方 pin されている前提で、RelativeFileName / FileIdentity を設定する。
    /// </summary>
    public unsafe void PatchPointers(char* pNames, CF_PLACEHOLDER_CREATE_INFO* pPlaceholders)
    {
        for (int i = 0; i < Count; i++)
        {
            var p = pNames + NameOffsets[i];
            pPlaceholders[i].RelativeFileName = p;
            pPlaceholders[i].FileIdentity = p;
        }
    }

    public void Dispose()
    {
        if (Names is not null) ArrayPool<char>.Shared.Return(Names);
        if (Placeholders is not null) ArrayPool<CF_PLACEHOLDER_CREATE_INFO>.Shared.Return(Placeholders, clearArray: true);
        if (NameOffsets is not null) ArrayPool<int>.Shared.Return(NameOffsets);
        if (NameByteLengths is not null) ArrayPool<int>.Shared.Return(NameByteLengths);
        Names = null!;
        Placeholders = null!;
        NameOffsets = null!;
        NameByteLengths = null!;
        Count = 0;
    }
}
