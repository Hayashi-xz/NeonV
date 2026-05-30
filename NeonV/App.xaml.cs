using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NeonV;

public partial class App : Application
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string appName = "NeonV_SingleInstance_Mutex";

        _mutex = new Mutex(true, appName, out bool createdNew);

        if (!createdNew)
        {
            try
            {
                var showEvent = EventWaitHandle.OpenExisting("NeonV_ShowEvent");
                showEvent.Set();
            }
            catch { }

            Environment.Exit(0);
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "NeonV_ShowEvent");

        Task.Run(() =>
        {
            while (true)
            {
                _showEvent.WaitOne();

                Current.Dispatcher.Invoke(() =>
                {
                    if (Current.MainWindow is MainWindow mainWindow)
                    {
                        mainWindow.Show();
                        mainWindow.ShowInTaskbar = true;

                        if (mainWindow.WindowState == WindowState.Minimized)
                            mainWindow.WindowState = WindowState.Normal;

                        mainWindow.Activate();
                        mainWindow.Focus();
                    }
                });
            }
        });

        base.OnStartup(e);
    }
}
