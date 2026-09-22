using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace NeonMix;

public sealed class Fader : FrameworkElement
{
    public double Value { get; private set; }
    public bool Muted { get; private set; }
    public Color Accent { get; set; }
    public event Action<double>? Changed;
    double peak, dragY, dragValue;
    public Fader() { Width = 128; Height = 266; Focusable = true; Cursor = Cursors.SizeNS; ToolTip = "볼륨 · 위아래 드래그 / 방향키"; }
    public void Update(double value, bool muted) { if (!IsMouseCaptured) Value = value; Muted = muted; InvalidateVisual(); }
    public void UpdatePeak(double value) { peak = Math.Clamp(value, 0, 1); InvalidateVisual(); }
    void Set(double value) { Value = Math.Clamp(value, 0, 100); InvalidateVisual(); Changed?.Invoke(Value); }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus(); var p = e.GetPosition(this); double thumbY = 24 + (100 - Value) * 2.12;
        if (Math.Abs(p.Y - thumbY) > 20) Set((236 - p.Y) / 2.12);
        dragY = p.Y; dragValue = Value; CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Set(dragValue + (dragY - e.GetPosition(this).Y) / 2.12); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); e.Handled = true; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Right) Set(Value + 1);
        else if (e.Key is Key.Down or Key.Left) Set(Value - 1);
        else if (e.Key == Key.Home) Set(0);
        else if (e.Key == Key.End) Set(100);
        else { base.OnKeyDown(e); return; }
        e.Handled = true;
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var ink = new SolidColorBrush(Accent);
        void Text(string s, double x, double y, double size = 9, string color = "#899097") => dc.DrawText(new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Consolas"), size, MixerWindow.Brush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        dc.DrawRoundedRectangle(MixerWindow.Brush("#0C0E10"), new Pen(MixerWindow.Brush("#3D4248"), 1), new Rect(40, 17, 10, 226), 4, 4);
        for (int i = 0; i <= 10; i++)
        {
            double y = 24 + i * 21.2;
            dc.DrawLine(new Pen(MixerWindow.Brush(i % 2 == 0 ? "#737B83" : "#474D54"), 1), new Point(15, y), new Point(i % 2 == 0 ? 32 : 27, y));
            if (i % 2 == 0) Text((100 - i * 10).ToString(), 1, y - 5, 8);
        }
        dc.DrawRoundedRectangle(MixerWindow.Brush("#0C1011"), new Pen(MixerWindow.Brush("#3C4446"), 1), new Rect(80, 17, 23, 226), 2, 2);
        double db = peak <= 0 ? -60 : Math.Max(-60, 20 * Math.Log10(peak));
        for (int i = 0; i < 40; i++)
        {
            bool lit = !Muted && db > -60 + i * 1.5;
            string color = i > 36 ? (lit ? "#EF7462" : "#362420") : i > 30 ? (lit ? "#DFC96B" : "#353223") : (lit ? "#7DBE8B" : "#23332B");
            dc.DrawRectangle(MixerWindow.Brush(color), null, new Rect(84, 234 - i * 5.4, 15, 3.4));
        }
        for (int i = 0; i <= 4; i++) Text(i == 4 ? "−∞" : (i * -15).ToString(), 109, 18 + i * 54, 8);
        double faderY = 24 + (100 - Value) * 2.12;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)), null, new Rect(25, faderY - 13, 43, 38), 3, 3);
        dc.DrawRoundedRectangle(new LinearGradientBrush(MixerWindow.Parse("#CFD3D5"), MixerWindow.Parse("#747D85"), 90), new Pen(MixerWindow.Brush("#16191C"), 1), new Rect(22, faderY - 18, 44, 36), 3, 3);
        for (int i = -2; i <= 2; i++) dc.DrawLine(new Pen(MixerWindow.Brush(i == 0 ? "#1A2024" : "#929CA3"), i == 0 ? 3 : 1), new Point(27, faderY + i * 5), new Point(61, faderY + i * 5));
        dc.DrawRectangle(Muted ? MixerWindow.Brush("#71777A") : ink, null, new Rect(22, faderY - 1, 5, 2));
        Text("VOL %", 25, 251); Text("PEAK", 80, 251);
        if (IsKeyboardFocused) dc.DrawRoundedRectangle(null, new Pen(ink, 1), new Rect(19, 10, 51, 238), 4, 4);
    }
}
