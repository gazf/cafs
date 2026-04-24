namespace CfApi.Interop;

[Flags]
public enum OplockOpenFlags : uint
{
    None = 0,
    Exclusive = 0x1,
    WriteAccess = 0x2,
    DeleteAccess = 0x4,
    ForegroundPriority = 0x8,
}
