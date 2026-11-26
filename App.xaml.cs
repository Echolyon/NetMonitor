using System.Threading;
using System.Windows;

namespace NetMonitor;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private NetMonitor.MainWindow? _main;
    private bool _errorShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Tek kopya: ikinci kez açılırsa mevcut pencereyi öne getirip kendini kapatır.
        _mutex = new Mutex(true, "NetMonitor.SingleInstance", out bool isFirst);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "NetMonitor.ShowWindow");

        if (!isFirst)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Write("Arayüz hatası", args.Exception);
            args.Handled = true; // izleme devam etsin

            if (!_errorShown)
            {
                _errorShown = true;
                MessageBox.Show(
                    "Beklenmeyen bir hata oluştu, uygulama çalışmaya devam ediyor.\n\n" +
                    args.Exception.Message + "\n\nAyrıntı: " + AppLog.FilePath,
                    "NetMonitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Write("Kritik hata", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write("Arka plan görevi hatası", args.Exception);
            args.SetObserved();
        };

        _main = new NetMonitor.MainWindow();
        MainWindow = _main;
        _main.Show();

        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(new Action(() => _main?.ShowFromTray()));
        })
        { IsBackground = true };
        listener.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
