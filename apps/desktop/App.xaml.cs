using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Ovrix;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args is ["--smoke", var folder] && Path.IsPathFullyQualified(folder))
        {
            AppContext.SetData("OVRIX.DataFolder", folder);
            var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
                { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (MainWindow is { IsVisible: true, ActualWidth: > 0 } window) window.Close();
                else Shutdown(7);
            };
            timer.Start();
        }
        else if (e.Args.Length == 1 && Path.IsPathFullyQualified(e.Args[0]))
            AppContext.SetData("OVRIX.File", e.Args[0]);
        base.OnStartup(e);
    }
}
