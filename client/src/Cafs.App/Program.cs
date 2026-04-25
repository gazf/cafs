using System.Windows.Forms;
using Cafs.App.Config;
using Cafs.App.Ui;
using Cafs.App.Util;

namespace Cafs.App;

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
