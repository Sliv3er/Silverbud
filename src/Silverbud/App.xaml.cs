using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Silverbud.Core;

namespace Silverbud;

public partial class App : Application
{
    private const string InstanceName = "Silverbud.SingleInstance";
    private const string ActivateName = "Silverbud.Activate";

    private Mutex? _instance;
    private EventWaitHandle? _activate;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--probe-audio"))
        {
            foreach (var line in AudioConnector.Describe())
                Log.Write($"probe: {line}");
            Shutdown();
            return;
        }

        _instance = new Mutex(true, InstanceName, out bool firstInstance);
        if (!firstInstance)
        {
            // Already running (maybe in the tray): bring that window forward instead.
            try { EventWaitHandle.OpenExisting(ActivateName).Set(); } catch { }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write($"Unhandled UI error: {args.Exception}");
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write($"Unobserved task error: {args.Exception}");
            args.SetObserved();
        };
        Log.Write($"Silverbud started ({string.Join(' ', e.Args)})");

        var window = new MainWindow(demo: e.Args.Contains("--demo"));
        MainWindow = window;

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateName);
        new Thread(() =>
        {
            while (_activate.WaitOne())
                Dispatcher.BeginInvoke(window.ShowFromTray);
        }) { IsBackground = true, Name = "Activation listener" }.Start();

        if (!e.Args.Contains("--tray"))
            window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
