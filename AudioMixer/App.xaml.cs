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

        base.OnStartup(e);
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
