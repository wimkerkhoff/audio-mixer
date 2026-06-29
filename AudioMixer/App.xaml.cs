using System.Threading;
using System.Windows;
using AudioMixer.Audio;

namespace AudioMixer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    // Two instances would both open WASAPI capture on the same input devices and fight over them.
    // A machine-scoped named mutex makes the second launch bail; an EventWaitHandle lets it poke the
    // first instance to surface its window so the user isn't left wondering why nothing happened.
    private const string InstanceName = "AudioMixer.SingleInstance.v1";

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showEvent;
    private Thread? _showListener;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceName, out bool isFirst);
        _ownsMutex = isFirst;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".show");

        if (!isFirst)
        {
            _showEvent.Set();
            AudioLog.Write("Second instance detected — signalling existing window and exiting.");
            // Exit immediately rather than via Shutdown(): the WPF dispatcher loop hasn't started yet,
            // so Shutdown() would leave the duplicate process lingering for a second or two. Nothing to
            // clean up on this path — we never owned the mutex or started the engine.
            _showEvent.Dispose();
            _instanceMutex.Dispose();
            Environment.Exit(0);
            return;
        }

        _showListener = new Thread(ShowListenerLoop) { IsBackground = true, Name = "SingleInstanceListener" };
        _showListener.Start();

        ApplyCliFlags(e.Args);
        base.OnStartup(e);
    }

    // Command-line equivalents of the AUDIOMIXER_LOG / AUDIOMIXER_STATE env vars, so a desktop
    // shortcut can enable diagnostics via arguments (a .lnk can't set env vars). Env vars still work;
    // these are additive. Runs before base.OnStartup so MainViewModel sees the state port on startup.
    //   --log            enable file logging (%TEMP%\AudioMixer.log)
    //   --state[=PORT]   enable the loopback JSON state endpoint (default port 7077)
    private static void ApplyCliFlags(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--log", StringComparison.OrdinalIgnoreCase))
            {
                AudioLog.Enabled = true;
            }
            else if (a.Equals("--state", StringComparison.OrdinalIgnoreCase) ||
                     a.StartsWith("--state=", StringComparison.OrdinalIgnoreCase))
            {
                string port = "7077";
                int eq = a.IndexOf('=');
                if (eq >= 0) port = a[(eq + 1)..];
                else if (i + 1 < args.Length && int.TryParse(args[i + 1], out _)) port = args[++i];
                Environment.SetEnvironmentVariable("AUDIOMIXER_STATE", port);
            }
        }
    }

    private void ShowListenerLoop()
    {
        while (_showEvent!.WaitOne())
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (MainWindow is { } w)
                {
                    if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                    w.Show();
                    w.Activate();
                    w.Topmost = true;
                    w.Topmost = false;
                }
            });
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        _showEvent?.Dispose();
        base.OnExit(e);
    }
}
