using System.Windows.Forms;
using Cafs.Client.Config;
using Cafs.Client.Ui;
using Cafs.Client.Util;

namespace Cafs.Client;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        FileLogger.Initialize();

        ApplicationConfiguration.Initialize();

        var settings = AppSettings.Load(args);

        using var context = new TrayAppContext(settings);
        Application.Run(context);
    }
}
