using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioMixer.Audio;
using AudioMixer.Audio.Replay;

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

    // A --replay instance is a dev sandbox, not the operator's mixer: it must be able to run
    // *alongside* a live session (which the single-instance guard would otherwise block), and it must
    // never write the operator's preset or grab their output devices. See ReplayOptions.
    private const string ReplayInstanceName = "AudioMixer.SingleInstance.Replay.v1";

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showEvent;
    private Thread? _showListener;

    protected override void OnStartup(StartupEventArgs e)
    {
        ApplyCliFlags(e.Args);
        InstallCrashHandlers();

        string instanceName = ReplayOptions.Current == null ? InstanceName : ReplayInstanceName;
        _instanceMutex = new Mutex(initiallyOwned: true, instanceName, out bool isFirst);
        _ownsMutex = isFirst;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".show");

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

        // Must be enabled before any window is created, or bindings resolved during startup are missed.
        if (AudioLog.Enabled) Services.BindingErrorListener.Enable();

        base.OnStartup(e);
        CreateWindows();
    }

    // A crash used to leave nothing behind but a WER bucket: there was no handler anywhere, AudioLog
    // is opt-in, and WPF swallows the managed stack. The crash file is written unconditionally (not
    // through AudioLog) because the one run that matters is the one nobody remembered to pass --log to.
    private void InstallCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, a) => RecordCrash("AppDomain", a.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            RecordCrash("UnobservedTask", a.Exception);
            a.SetObserved();
        };
        DispatcherUnhandledException += (_, a) => RecordCrash("Dispatcher", a.Exception);
    }

    private static void RecordCrash(string source, Exception? ex)
    {
        string text = $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {source} unhandled ==={Environment.NewLine}{ex}{Environment.NewLine}";
        AudioLog.Write(text);
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioMixer.crash.log"), text);
        }
        catch { /* a crash logger that throws is worse than no crash logger */ }
    }

    // Command-line equivalents of the AUDIOMIXER_LOG / AUDIOMIXER_STATE env vars, so a desktop
    // shortcut can enable diagnostics via arguments (a .lnk can't set env vars). Env vars still work;
    // these are additive. Runs before base.OnStartup so MainViewModel sees the state port on startup.
    //   --log            enable file logging (%TEMP%\AudioMixer.log)
    //   --state[=PORT]   enable the loopback JSON state endpoint (default port 7077)
    //   --replay[=STAMP] replay a recorded session instead of live mics (sandbox; see ReplayOptions)
    //   --speed=N        replay rate multiplier (batch runs); --loop  replay repeatedly
    //   --advanced       open the full mixer instead of the operator (Simple) panel
    //   --shots[=DIR]    render every window to PNG and exit (works with the workstation locked)
    private static void ApplyCliFlags(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--log", StringComparison.OrdinalIgnoreCase))
            {
                AudioLog.Enabled = true;
            }
            else if (a.Equals("--replay", StringComparison.OrdinalIgnoreCase) ||
                     a.StartsWith("--replay=", StringComparison.OrdinalIgnoreCase))
            {
                int eq = a.IndexOf('=');
                ReplayOptions.Current = new ReplayOptions { Stamp = eq >= 0 ? a[(eq + 1)..] : null };
            }
            else if (a.StartsWith("--speed=", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(a[8..], out double sp)) ReplayOptions.Speed = sp;
            }
            else if (a.Equals("--loop", StringComparison.OrdinalIgnoreCase))
            {
                ReplayOptions.Loop = true;
            }
            else if (a.Equals("--simple", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--ui=simple", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--ui=new", StringComparison.OrdinalIgnoreCase))
            {
                _useSimpleUi = true;
            }
            else if (a.Equals("--advanced", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--ui=advanced", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--ui=classic", StringComparison.OrdinalIgnoreCase))
            {
                _useSimpleUi = false;
            }
            else if (a.StartsWith("--scene=", StringComparison.OrdinalIgnoreCase))
            {
                if (Enum.TryParse<Models.Scene>(a[8..], ignoreCase: true, out var sc)) StartupScene = sc;
            }
            else if (a.StartsWith("--shots", StringComparison.OrdinalIgnoreCase))
            {
                int eq = a.IndexOf('=');
                _shotsDirectory = eq > 0 ? a[(eq + 1)..] : System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "AudioMixer-shots");
                _openAllWindows = true;   // capturing one window is never what this is for
            }
            else if (a.Equals("--open-all", StringComparison.OrdinalIgnoreCase))
            {
                _useSimpleUi = true;
                _openAllWindows = true;
            }
            else if (a.StartsWith("--seek=", StringComparison.OrdinalIgnoreCase))
            {
                ReplayOptions.Seek = ReplayOptions.ParseTime(a[7..]);
            }
            else if (a.StartsWith("--for=", StringComparison.OrdinalIgnoreCase))
            {
                ReplayOptions.Duration = ReplayOptions.ParseTime(a[6..]);
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

    /// <summary>
    /// Simple mode is the default: a plain launch opens the operator panel, and Advanced is reachable
    /// both from a button on that panel and from <c>--advanced</c> / <c>--ui=advanced</c>. Baseline
    /// replay runs pass <c>--advanced</c> so they keep the window their goldens were recorded under.
    /// Both windows bind the SAME view model, which is what makes running them side by side a valid
    /// comparison — they cannot disagree about mixer state.
    /// </summary>
    private static bool _useSimpleUi = true;
    private static string? _shotsDirectory;

    private void CreateWindows()
    {
        var main = new MainWindow();
        MainWindow = main;

        if (!_useSimpleUi)
        {
            main.Show();
            return;
        }

        var simple = new Views.SimpleWindow(main.ViewModel) { AdvancedWindow = main };

        // Advanced starts hidden rather than closed: closing it would dispose the shared view model.
        // With it hidden, the app must not exit on "last window closed", so shutdown is explicit.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        simple.Closed += (_, _) =>
        {
            main.Close();      // disposes the view model, stopping the engine cleanly
            Shutdown();
        };
        simple.Show();
        MainWindow = simple;   // so the single-instance signal raises the panel the operator is using

        // Diagnostics and Settings only resolve their bindings when opened, so a smoke run has to open
        // them or their markup is untested. Paired with --log this turns "did I break a binding" into a
        // one-command check across every window.
        if (_openAllWindows)
        {
            main.Show();
            simple.OpenAuxiliaryWindows();
        }

        if (_shotsDirectory != null) CaptureWindowsThenExit(_shotsDirectory);
    }

    /// <summary>
    /// Renders every open window to a PNG and exits.
    ///
    /// Uses RenderTargetBitmap on the visual tree rather than a screen grab, which is the whole point:
    /// it works when the window is behind others, off-screen, or — the case that prompted it — when the
    /// workstation is LOCKED, where CopyFromScreen returns the lock screen instead of the desktop. It
    /// captures the client area, not the title bar, which is what you want for documenting a UI anyway.
    ///
    /// Also the honest way to check a UI change actually looks right without being at the machine.
    /// </summary>
    private void CaptureWindowsThenExit(string directory)
    {
        var timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle)
        {
            // Devices enumerate, meters settle and the Checks window decides whether to open itself.
            // Rendering before that photographs a half-built panel.
            Interval = TimeSpan.FromSeconds(8),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            int n = 0;
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                foreach (Window w in Windows.Cast<Window>().ToList())
                {
                    if (!w.IsLoaded || w.ActualWidth < 1 || w.ActualHeight < 1)
                    {
                        AudioLog.Write($"Skipped '{w.Title}': loaded={w.IsLoaded} " +
                                       $"size={w.ActualWidth:F0}x{w.ActualHeight:F0}");
                        continue;
                    }
                    var name = Sanitise(string.IsNullOrWhiteSpace(w.Title) ? w.GetType().Name : w.Title);
                    var path = System.IO.Path.Combine(directory, $"{++n:00}-{name}.png");
                    Capture(w, path);
                    AudioLog.Write($"Captured {w.Title} -> {path}");
                }
            }
            catch (Exception ex)
            {
                AudioLog.Write($"Window capture failed: {ex.GetType().Name}: {ex.Message}");
            }
            Shutdown();
        };
        timer.Start();
    }

    private static void Capture(Window w, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(w);
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(w.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(w.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(w);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
    }

    private static string Sanitise(string name)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
        return name.Replace(' ', '-').Replace("—", "-").Trim('-');
    }

    private static bool _openAllWindows;

    /// <summary>
    /// Applied once the view model is up. Lets a desktop shortcut open straight into a scene, and lets
    /// a smoke run assert the whole scene path (pure transform -> view models -> engine) from /state.
    /// </summary>
    public static Models.Scene? StartupScene { get; private set; }

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
