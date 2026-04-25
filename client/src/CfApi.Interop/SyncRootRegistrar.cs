using System.Runtime.CompilerServices;
using CfApi.Native;

namespace CfApi.Interop;

public static class SyncRootRegistrar
{
    public static unsafe void Register(SyncRootOptions options)
    {
        Directory.CreateDirectory(options.SyncRootPath);

        // 既存登録をクリアして再登録可能に。
        CldApi.CfUnregisterSyncRoot(options.SyncRootPath);

        fixed (char* providerName = options.ProviderName)
        fixed (char* providerVersion = options.ProviderVersion)
        {
            var registration = new CF_SYNC_REGISTRATION
            {
                StructSize = (uint)Unsafe.SizeOf<CF_SYNC_REGISTRATION>(),
                ProviderName = providerName,
                ProviderVersion = providerVersion,
                SyncRootIdentity = null,
                SyncRootIdentityLength = 0,
                FileIdentity = null,
                FileIdentityLength = 0,
                ProviderId = options.ProviderId,
            };

            var policies = new CF_SYNC_POLICIES
            {
                StructSize = (uint)Unsafe.SizeOf<CF_SYNC_POLICIES>(),
                Hydration = new CF_HYDRATION_POLICY
                {
                    Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_FULL,
                    Modifier = CF_HYDRATION_POLICY_MODIFIER.CF_HYDRATION_POLICY_MODIFIER_NONE,
                },
                Population = new CF_POPULATION_POLICY
                {
                    Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_ALWAYS_FULL,
                    Modifier = CF_POPULATION_POLICY_MODIFIER.CF_POPULATION_POLICY_MODIFIER_NONE,
                },
                InSync = CF_INSYNC_POLICY.CF_INSYNC_POLICY_TRACK_ALL,
                HardLink = CF_HARDLINK_POLICY.CF_HARDLINK_POLICY_NONE,
                PlaceholderManagement = CF_PLACEHOLDER_MANAGEMENT_POLICY.CF_PLACEHOLDER_MANAGEMENT_POLICY_CONVERT_TO_UNRESTRICTED,
            };

            var hr = CldApi.CfRegisterSyncRoot(
                options.SyncRootPath,
                &registration,
                &policies,
                CF_REGISTER_FLAGS.CF_REGISTER_FLAG_NONE);

            if (CldApi.Failed(hr))
                throw new InvalidOperationException($"CfRegisterSyncRoot failed: 0x{hr:X8}");
        }
    }

    public static void Unregister(string syncRootPath)
    {
        CldApi.CfUnregisterSyncRoot(syncRootPath);
    }
}
