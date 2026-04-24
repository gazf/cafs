namespace CfApi.Interop;

[Flags]
public enum ConvertFlags : uint
{
    None = 0,
    MarkInSync = 0x1,
    Dehydrate = 0x2,
    EnableOnDemandPopulation = 0x4,
    AlwaysFull = 0x8,
}
