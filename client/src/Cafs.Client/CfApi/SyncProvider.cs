using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;
using Cafs.Client.CfApi.Callbacks;
using Cafs.Client.Http;

namespace Cafs.Client.CfApi;

public class SyncProvider : IDisposable
{
    private CF_CONNECTION_KEY _connectionKey;
    private bool _connected;
    private readonly CafsHttpClient _httpClient;
    private readonly string _syncRootPath;

    // Keep delegates alive to prevent GC collection
    private readonly CF_CALLBACK _fetchPlaceholdersDelegate;
    private readonly CF_CALLBACK _fetchDataDelegate;
    private readonly CF_CALLBACK _cancelFetchDataDelegate;
    private readonly CF_CALLBACK _notifyDeleteDelegate;

    private readonly FetchPlaceholdersHandler _fetchPlaceholders;
    private readonly FetchDataHandler _fetchData;
    private readonly CancelFetchDataHandler _cancelFetchData;
    private readonly NotifyDeleteHandler _notifyDelete;

    public SyncProvider(CafsHttpClient httpClient, string syncRootPath)
    {
        _httpClient = httpClient;
        _syncRootPath = syncRootPath;

        _fetchPlaceholders = new FetchPlaceholdersHandler(httpClient, syncRootPath);
        _fetchData = new FetchDataHandler(httpClient, syncRootPath);
        _cancelFetchData = new CancelFetchDataHandler();
        _notifyDelete = new NotifyDeleteHandler(httpClient, syncRootPath);

        _fetchPlaceholdersDelegate = _fetchPlaceholders.OnCallback;
        _fetchDataDelegate = _fetchData.OnCallback;
        _cancelFetchDataDelegate = _cancelFetchData.OnCallback;
        _notifyDeleteDelegate = _notifyDelete.OnCallback;
    }

    public void Connect()
    {
        var callbackTable = new CF_CALLBACK_REGISTRATION[]
        {
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS,
                Callback = _fetchPlaceholdersDelegate
            },
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA,
                Callback = _fetchDataDelegate
            },
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_DATA,
                Callback = _cancelFetchDataDelegate
            },
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DELETE,
                Callback = _notifyDeleteDelegate
            },
            CF_CALLBACK_REGISTRATION.CF_CALLBACK_REGISTRATION_END
        };

        var hr = CfConnectSyncRoot(
            _syncRootPath,
            callbackTable,
            IntPtr.Zero,
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO |
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH,
            out _connectionKey
        );

        if (hr.Failed)
        {
            throw new InvalidOperationException(
                $"CfConnectSyncRoot failed: 0x{hr:X8}");
        }

        _connected = true;
        Console.WriteLine("Sync provider connected.");
    }

    public void Disconnect()
    {
        if (_connected)
        {
            CfDisconnectSyncRoot(_connectionKey);
            _connected = false;
            Console.WriteLine("Sync provider disconnected.");
        }
    }

    public void Dispose()
    {
        Disconnect();
    }
}
