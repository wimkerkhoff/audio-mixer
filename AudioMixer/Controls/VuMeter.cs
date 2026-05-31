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

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

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

        if (Orientation == Orientation.Vertical)
        {
            DrawVertical(dc, size, peakFrac, holdFrac);
        }
        else
        {
            DrawHorizontal(dc, size, peakFrac, holdFrac);
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
