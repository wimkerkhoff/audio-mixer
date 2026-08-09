using System.Windows;
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
        _timer.Start();

        Closed += (_, _) => _timer.Stop();
        _vm.RefreshDiagnostics();

        // Row count is the positive signal a smoke run needs: "no binding errors" is also what a
        // window that never opened would report.
        Audio.AudioLog.Write($"Diagnostics window opened ({_vm.DiagnosticRows.Count} rows).");
    }
}
