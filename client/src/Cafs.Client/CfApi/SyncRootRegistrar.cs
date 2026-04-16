using System.Runtime.InteropServices;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace Cafs.Client.CfApi;

public static class SyncRootRegistrar
{
    private const string ProviderId = "CAFS";
    private const string ProviderVersion = "1.0";

    public static void Register(string syncRootPath, string displayName)
    {
        if (!Directory.Exists(syncRootPath))
        {
            Directory.CreateDirectory(syncRootPath);
        }

        var registration = new CF_SYNC_REGISTRATION
        {
            StructSize = (uint)Marshal.SizeOf<CF_SYNC_REGISTRATION>(),
            ProviderName = ProviderId,
            ProviderVersion = ProviderVersion,
            ProviderId = Guid.Parse("B5F2A9C1-4E7D-4A3B-8F6C-1D2E3F4A5B6C")
        };

        var policies = new CF_SYNC_POLICIES
        {
            StructSize = (uint)Marshal.SizeOf<CF_SYNC_POLICIES>(),
            Hydration = new CF_HYDRATION_POLICY
            {
                Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_FULL,
                Modifier = CF_HYDRATION_POLICY_MODIFIER.CF_HYDRATION_POLICY_MODIFIER_NONE
            },
            Population = new CF_POPULATION_POLICY
            {
                Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_FULL,
                Modifier = CF_POPULATION_POLICY_MODIFIER.CF_POPULATION_POLICY_MODIFIER_NONE
            },
            InSync = CF_INSYNC_POLICY.CF_INSYNC_POLICY_TRACK_ALL,
            HardLink = CF_HARDLINK_POLICY.CF_HARDLINK_POLICY_NONE,
            PlaceholderManagement = CF_PLACEHOLDER_MANAGEMENT_POLICY.CF_PLACEHOLDER_MANAGEMENT_POLICY_DEFAULT
        };

        var hr = CfRegisterSyncRoot(
            syncRootPath,
            registration,
            policies,
            CF_REGISTER_FLAGS.CF_REGISTER_FLAG_NONE
        );

        if (hr.Failed)
        {
            throw new InvalidOperationException(
                $"CfRegisterSyncRoot failed: 0x{hr:X8}");
        }

        Console.WriteLine($"Sync root registered: {syncRootPath}");
    }

    public static void Unregister(string syncRootPath)
    {
        var hr = CfUnregisterSyncRoot(syncRootPath);
        if (hr.Failed)
        {
            Console.WriteLine($"CfUnregisterSyncRoot warning: 0x{hr:X8}");
        }
        else
        {
            Console.WriteLine($"Sync root unregistered: {syncRootPath}");
        }
    }
}
