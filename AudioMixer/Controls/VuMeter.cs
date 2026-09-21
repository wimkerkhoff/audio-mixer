using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AudioMixer.Controls;

public sealed class VuMeter : Control
{
    public static readonly DependencyProperty PeakDbProperty =
        DependencyProperty.Register(nameof(PeakDb), typeof(double), typeof(VuMeter),
            new FrameworkPropertyMetadata(-120.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoldDbProperty =
        DependencyProperty.Register(nameof(HoldDb), typeof(double), typeof(VuMeter),
            new FrameworkPropertyMetadata(-120.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Draws the band where speech should sit. On 2026-09-20 a whole meeting ran ~22 dB under target
    /// because level was a number in a window nobody had open; as a band it becomes a SHAPE — the bar
    /// falls short of the stripe — which needs no understanding of decibels to read.
    /// </summary>
    public static readonly DependencyProperty ShowTargetBandProperty =
        DependencyProperty.Register(nameof(ShowTargetBand), typeof(bool), typeof(VuMeter),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OrientationProperty =
        DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(VuMeter),
            new FrameworkPropertyMetadata(Orientation.Vertical, FrameworkPropertyMetadataOptions.AffectsRender));

    public double PeakDb
    {
        get => (double)GetValue(PeakDbProperty);
        set => SetValue(PeakDbProperty, value);
    }

    public double HoldDb
    {
        get => (double)GetValue(HoldDbProperty);
        set => SetValue(HoldDbProperty, value);
    }

    public bool ShowTargetBand
    {
        get => (bool)GetValue(ShowTargetBandProperty);
        set => SetValue(ShowTargetBandProperty, value);
    }

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>Matches ChannelViewModel.TargetDb / TargetHalfWidthDb — change them together.</summary>
    private const double TargetDb = -24.0;
    private const double TargetHalfWidthDb = 6.0;

    private const double MinDb = -60.0;
    private const double MaxDb = 0.0;
    private const double YellowDb = -12.0;
    private const double RedDb = -3.0;

    static VuMeter()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(VuMeter), new FrameworkPropertyMetadata(typeof(VuMeter)));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        if (size.Width <= 0 || size.Height <= 0) return;

        var bgBrush = new SolidColorBrush(Color.FromRgb(20, 20, 24));
        dc.DrawRectangle(bgBrush, null, new Rect(0, 0, size.Width, size.Height));

        double peak = Math.Clamp(PeakDb, MinDb, MaxDb);
        double hold = Math.Clamp(HoldDb, MinDb, MaxDb);

        double peakFrac = (peak - MinDb) / (MaxDb - MinDb);
        double holdFrac = (hold - MinDb) / (MaxDb - MinDb);

        // Behind the level, so a bar reaching the band still shows it rather than hiding the goal.
        if (ShowTargetBand) DrawTargetBand(dc, size);

        if (Orientation == Orientation.Vertical)
        {
            DrawVertical(dc, size, peakFrac, holdFrac);
        }
        else
        {
            DrawHorizontal(dc, size, peakFrac, holdFrac);
        }
    }

    private void DrawTargetBand(DrawingContext dc, Size size)
    {
        static double Frac(double db) => (Math.Clamp(db, MinDb, MaxDb) - MinDb) / (MaxDb - MinDb);
        double lo = Frac(TargetDb - TargetHalfWidthDb);
        double hi = Frac(TargetDb + TargetHalfWidthDb);

        var fill = new SolidColorBrush(Color.FromArgb(56, 79, 163, 236));
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(150, 79, 163, 236)), 1);

        if (Orientation == Orientation.Horizontal)
        {
            double x = size.Width * lo, w = size.Width * (hi - lo);
            dc.DrawRectangle(fill, null, new Rect(x, 0, w, size.Height));
            dc.DrawLine(edge, new Point(x, 0), new Point(x, size.Height));
            dc.DrawLine(edge, new Point(x + w, 0), new Point(x + w, size.Height));
        }
        else
        {
            double y = size.Height * (1 - hi), h = size.Height * (hi - lo);
            dc.DrawRectangle(fill, null, new Rect(0, y, size.Width, h));
            dc.DrawLine(edge, new Point(0, y), new Point(size.Width, y));
            dc.DrawLine(edge, new Point(0, y + h), new Point(size.Width, y + h));
        }
    }

    private static void DrawVertical(DrawingContext dc, Size size, double peakFrac, double holdFrac)
    {
        double w = size.Width;
        double h = size.Height;
        double peakHeight = h * peakFrac;
        double yellowStart = h * ((YellowDb - MinDb) / (MaxDb - MinDb));
        double redStart = h * ((RedDb - MinDb) / (MaxDb - MinDb));

        if (peakHeight > 0)
        {
            double bottomY = h;

            double greenEnd = Math.Min(peakHeight, yellowStart);
            if (greenEnd > 0)
            {
                var rect = new Rect(0, bottomY - greenEnd, w, greenEnd);
                dc.DrawRectangle(Brushes.LimeGreen, null, rect);
            }
            if (peakHeight > yellowStart)
            {
                double yellowEnd = Math.Min(peakHeight, redStart);
                double yellowH = yellowEnd - yellowStart;
                if (yellowH > 0)
                {
                    var rect = new Rect(0, bottomY - yellowEnd, w, yellowH);
                    dc.DrawRectangle(Brushes.Gold, null, rect);
                }
            }
            if (peakHeight > redStart)
            {
                double redH = peakHeight - redStart;
                var rect = new Rect(0, bottomY - peakHeight, w, redH);
                dc.DrawRectangle(Brushes.Red, null, rect);
            }
        }

        if (holdFrac > 0)
        {
            double y = h - h * holdFrac;
            var pen = new Pen(Brushes.White, 1.5);
            dc.DrawLine(pen, new Point(0, y), new Point(w, y));
        }

        var tickPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 0.5);
        for (int db = -60; db <= 0; db += 6)
        {
            double frac = (db - MinDb) / (MaxDb - MinDb);
            double y = h - h * frac;
            dc.DrawLine(tickPen, new Point(0, y), new Point(w * 0.25, y));
        }
    }

    private static void DrawHorizontal(DrawingContext dc, Size size, double peakFrac, double holdFrac)
    {
        double w = size.Width;
        double h = size.Height;
        double peakWidth = w * peakFrac;
        double yellowStart = w * ((YellowDb - MinDb) / (MaxDb - MinDb));
        double redStart = w * ((RedDb - MinDb) / (MaxDb - MinDb));

        if (peakWidth > 0)
        {
            double greenEnd = Math.Min(peakWidth, yellowStart);
            if (greenEnd > 0)
            {
                dc.DrawRectangle(Brushes.LimeGreen, null, new Rect(0, 0, greenEnd, h));
            }
            if (peakWidth > yellowStart)
            {
                double yellowEnd = Math.Min(peakWidth, redStart);
                dc.DrawRectangle(Brushes.Gold, null, new Rect(yellowStart, 0, yellowEnd - yellowStart, h));
            }
            if (peakWidth > redStart)
            {
                dc.DrawRectangle(Brushes.Red, null, new Rect(redStart, 0, peakWidth - redStart, h));
            }
        }

        if (holdFrac > 0)
        {
            double x = w * holdFrac;
            var pen = new Pen(Brushes.White, 1.5);
            dc.DrawLine(pen, new Point(x, 0), new Point(x, h));
        }
    }
}
