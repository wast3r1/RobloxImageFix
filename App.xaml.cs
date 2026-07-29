using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace RobloxImageFix;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            File.WriteAllText("crash.log", $"Dispatcher: {e.Exception}");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            File.WriteAllText("crash.log", $"Domain: {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            File.WriteAllText("crash.log", $"Task: {e.Exception}");
            e.SetObserved();
        };
    }
}
