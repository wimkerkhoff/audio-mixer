using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AudioMixer.ViewModels;

namespace AudioMixer.Views;

public partial class DiagnosticsWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer;

    public DiagnosticsWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        // 10 Hz, not the 30 Hz meter tick: this rebuilds a ranked table, and nobody reads a selection
        // rationale faster than that. Polling the existing snapshot keeps it off the audio threads.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => _vm.RefreshDiagnostics();

        // Session and Devices change slowly and cost real work to build (a device list walks the
        // endpoints and reads their gain), so they refresh on tab change and on demand, never at 10 Hz.
        RefreshSlowTabs();
        var slow = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
        slow.Tick += (_, _) => RefreshSlowTabs();
        slow.Start();
        Closed += (_, _) => slow.Stop();
        _timer.Start();

        Closed += (_, _) => _timer.Stop();
        _vm.RefreshDiagnostics();

        // Row count is the positive signal a smoke run needs: "no binding errors" is also what a
        // window that never opened would report.
        Audio.AudioLog.Write($"Diagnostics window opened ({_vm.DiagnosticRows.Count} rows).");
    }

    private void RefreshSlowTabs()
    {
        DeviceGrid.ItemsSource = _vm.DeviceRows();
        BuildSessionTab();
    }

    /// <summary>
    /// The figures that took an afternoon of log-parsing on 2026-09-20, rendered from the live
    /// aggregate. Built in code rather than markup because the shape is a flat report, and a dozen
    /// nested ItemsControls would be harder to read than the loop that writes it.
    /// </summary>
    private void BuildSessionTab()
    {
        var s = _vm.SessionSnapshot;
        SessionPanel.Children.Clear();
        if (s == null)
        {
            SessionPanel.Children.Add(Cap("No session is being recorded (replay sandbox)."));
            return;
        }

        SessionPanel.Children.Add(Head($"{s.DurationMinutes:F1} min" +
            (s.Scene == null ? "" : $"  ·  {s.Scene}")));

        foreach (var o in s.Outputs)
        {
            SessionPanel.Children.Add(Head($"Bus {o.Label}"));
            SessionPanel.Children.Add(Cell(
                $"{o.Handoffs} hand-offs   {o.HandoffsPerMinute:F1}/min   no winner {o.NoWinnerPercent:F0}%"));
            foreach (var kv in o.OccupancyPercent.Where(k => k.Key >= 0).OrderByDescending(k => k.Value))
            {
                string who = kv.Key < s.Inputs.Count ? s.Inputs[kv.Key].Label : $"mic {kv.Key + 1}";
                SessionPanel.Children.Add(Cell($"   {who,-14} held {kv.Value,5:F1}%"));
            }
        }

        SessionPanel.Children.Add(Head("Microphones"));
        foreach (var i in s.Inputs)
        {
            string speech = float.IsNaN(i.SpeechDb) ? "  —" : $"{i.SpeechDb,4:F0}";
            SessionPanel.Children.Add(Cell(
                $"{i.Label,-14} speech {speech}  led {i.LeaderPercent,5:F1}%  " +
                $"gated {i.MutedByGatePercent,5:F1}%  clipped {i.ClippedSamples}  under {i.Underruns}"));
        }

        if (s.Events.Count > 0)
        {
            SessionPanel.Children.Add(Head("Events"));
            foreach (var e in s.Events)
                SessionPanel.Children.Add(Cell($"{e.TimeOfDay}  {e.Kind,-10} {e.Message}"));
        }

        var past = _vm.PastSessions;
        if (past.Count > 0)
        {
            SessionPanel.Children.Add(Head($"Earlier sessions ({past.Count} kept)"));
            foreach (var f in past.Take(8))
                SessionPanel.Children.Add(Cell($"{f.Stamp}   {f.Bytes / 1024.0,5:F1} KB"));
        }
    }

    private static TextBlock Head(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEE)),
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 8, 0, 3),
    };

    private static TextBlock Cell(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xDE)),
        FontFamily = new FontFamily("Consolas"),
        FontSize = 11,
    };

    private static TextBlock Cap(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0xA5)),
        FontSize = 10,
        TextWrapping = TextWrapping.Wrap,
    };
}
