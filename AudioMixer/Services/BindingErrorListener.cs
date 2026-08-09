using System.Diagnostics;
using AudioMixer.Audio;

namespace AudioMixer.Services;

/// <summary>
/// Routes WPF data-binding failures into the app log.
///
/// WPF resolves binding paths at *runtime* and swallows failures silently — a control bound to a
/// property that no longer exists simply renders blank, so a clean build proves nothing about the UI.
/// That is exactly how the per-bus LEDs could have shipped dead. With this on, a broken binding shows
/// up as a log line instead of as an operator noticing a missing light mid-service.
///
/// Enabled by <c>--log</c> / AUDIOMIXER_LOG, so it costs nothing in a normal run.
/// </summary>
public static class BindingErrorListener
{
    private static bool _enabled;

    public static int ErrorCount { get; private set; }

    public static void Enable()
    {
        if (_enabled) return;
        _enabled = true;

        // Refresh() must run before the switch level is raised, or the sources keep their default
        // (Off) level and nothing is ever reported.
        PresentationTraceSources.Refresh();
        var source = PresentationTraceSources.DataBindingSource;
        source.Listeners.Add(new LogListener());
        source.Switch.Level = SourceLevels.Warning | SourceLevels.Error | SourceLevels.Critical;
    }

    private sealed class LogListener : TraceListener
    {
        // TraceListener receives a message in fragments (Write) terminated by WriteLine, so buffer
        // until the line is complete rather than logging half a message per call.
        private readonly System.Text.StringBuilder _pending = new();

        public override void Write(string? message) => _pending.Append(message);

        public override void WriteLine(string? message)
        {
            _pending.Append(message);
            string line = _pending.ToString().Trim();
            _pending.Clear();
            if (line.Length == 0) return;
            ErrorCount++;
            AudioLog.Write($"[binding] {line}");
            Trace.WriteLine($"[binding] {line}");
        }
    }
}
