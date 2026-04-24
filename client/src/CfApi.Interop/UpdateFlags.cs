namespace CfApi.Interop;

[Flags]
public enum UpdateFlags : uint
{
    None = 0x000,
    VerifyInSync = 0x001,
    MarkInSync = 0x002,
    Dehydrate = 0x004,
    EnableOnDemandPopulation = 0x008,
    DisableOnDemandPopulation = 0x010,
    RemoveFileIdentity = 0x020,
    ClearInSync = 0x040,
    RemoveProperty = 0x080,
    PassthroughFsMetadata = 0x100,
    AlwaysFull = 0x200,
    AllowPartial = 0x400,
    RemoveUnrestrictedPin = 0x800,
}
