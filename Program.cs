using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;

namespace NeonMix;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--self-test")) { SelfTest.Run(); return; }
        if (args.Contains("--probe")) { SelfTest.Probe(); return; }
        using var mutex = new Mutex(true, args.Contains("--preview") || args.Contains("--smoke-test") ? "Local\\NeonMix.Validation" : "Local\\NeonMix.Desktop", out bool first);
        if (!first) { System.Windows.MessageBox.Show("Neon Mix가 이미 실행 중입니다. 트레이 아이콘이나 설정한 단축키로 열어 주세요.", "Neon Mix"); return; }
        var app = new Application();
        app.DispatcherUnhandledException += (_, e) => { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "error.log"), e.Exception + Environment.NewLine); System.Windows.MessageBox.Show(e.Exception.Message, "Neon Mix 오류"); e.Handled = true; };
        app.Run(new MixerWindow(args.Contains("--preview"), args.Contains("--smoke-test")));
    }
}

internal sealed class Preferences
{
    public int Hotkey { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeonMix", "settings.json");
    public static Preferences Load() { try { return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new(); } catch { return new(); } }
    public void Save() { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
}

internal sealed class ChannelCard : Border
{
    public Fader Fader = new();
    public TextBlock Detail;
    public Button Mute;
    readonly TextBlock valueText;
    public bool Interacting => Fader.IsMouseCaptured;
    public ChannelCard(string name, string key, Color accent, Action<double> volume, Action mute)
    {
        bool master = key == "master";
        Width = 148; Height = 448; Padding = new Thickness(9, 0, 9, 10);
        BorderThickness = new Thickness(1, 0, 1, 0); BorderBrush = MixerWindow.Brush("#151719");
        Background = new LinearGradientBrush(MixerWindow.Parse(master ? "#42484E" : "#363B40"), MixerWindow.Parse("#272C31"), 0);
        var stack = new StackPanel(); Child = stack;
        stack.Children.Add(new Border { Height = 4, Background = new SolidColorBrush(accent), Margin = new Thickness(-9, 0, -9, 10) });
        var title = MixerWindow.Label(master ? "MASTER" : name, 13); title.FontWeight = FontWeights.SemiBold; title.TextTrimming = TextTrimming.CharacterEllipsis; title.ToolTip = name; title.Height = 26; stack.Children.Add(title);
        Detail = MixerWindow.Label(master ? "기본 출력" : "앱 오디오", 10, MixerWindow.Brush("#A4ADB5")); Detail.Height = 21; Detail.TextTrimming = TextTrimming.CharacterEllipsis; stack.Children.Add(Detail);
        var readout = new Border { Background = MixerWindow.Brush("#161C20"), BorderBrush = MixerWindow.Brush("#50585E"), BorderThickness = new Thickness(1), Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 8, 0, 7) };
        valueText = MixerWindow.Label("100 %", 15, new SolidColorBrush(accent)); valueText.FontFamily = new FontFamily("Consolas"); valueText.HorizontalAlignment = HorizontalAlignment.Center; readout.Child = valueText; stack.Children.Add(readout);
        Fader.Accent = accent; Fader.Changed += volume; stack.Children.Add(Fader);
        Mute = MixerWindow.MakeButton("M  ·  음소거", mute); Mute.Margin = new Thickness(0, 5, 0, 7); Mute.HorizontalAlignment = HorizontalAlignment.Stretch; stack.Children.Add(Mute);
        var bottom = MixerWindow.Label(master ? "MAIN OUTPUT" : "APP CHANNEL", 9, new SolidColorBrush(accent)); bottom.HorizontalAlignment = HorizontalAlignment.Center; stack.Children.Add(bottom);
        System.Windows.Automation.AutomationProperties.SetName(Fader, name + " 볼륨 페이더");
        System.Windows.Automation.AutomationProperties.SetName(Mute, name + " 음소거 전환");
    }
    public void Update(double value, bool muted, string detail)
    {
        Fader.Update(value, muted); valueText.Text = $"{value:0} %"; Detail.Text = detail;
        Mute.Content = muted ? "M  ·  음소거 해제" : "M  ·  음소거";
        Mute.Background = MixerWindow.Brush(muted ? "#997846" : "#20262B");
        Mute.Foreground = MixerWindow.Brush(muted ? "#FFFFFF" : "#B9C2CA");
    }
}
internal sealed class MixerWindow : Window
{
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr window, int id, uint mods, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window, int id);
    readonly Preferences prefs = Preferences.Load();
    readonly StackPanel channels = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
    readonly Border masterHost = new();
    readonly ScrollViewer channelScroll = new();
    readonly DispatcherTimer meterTimer = new() { Interval = TimeSpan.FromMilliseconds(65) };
    readonly Dictionary<string, ChannelCard> cards = new();
    readonly TextBlock status = Label("연결 중…", 12, Brush("#A5B2C5"));
    readonly TextBlock count = Label("", 12, Brush("#93A1B6"));
    readonly TextBlock empty = Label("앱에서 소리를 재생하면 여기에 자동으로 나타납니다.", 14, Brush("#A5B2C5"));
    readonly TextBlock shortcut = Label("", 12, Brush("#A5B2C5"));
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Forms.NotifyIcon tray;
    readonly bool preview;
    AudioSnapshot snapshot = new();
    IntPtr handle; HwndSource? source; bool quitting, hotkeyRegistered;
    readonly string[] shortcuts = ["Ctrl + Alt + M", "Ctrl + Alt + F9", "Ctrl + Alt + F10", "Alt + Shift + M"];
    readonly Color[] palette = [Parse("#61E5CD"), Parse("#B4A0FF"), Parse("#FFBC83"), Parse("#73C8FF"), Parse("#EF9ACA")];
    public static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    public static SolidColorBrush Brush(string hex) => new(Parse(hex));
    public static TextBlock Label(string text, double size = 14, System.Windows.Media.Brush? foreground = null) => new() { Text = text, FontSize = size, Foreground = foreground ?? Brush("#F0F4FA"), VerticalAlignment = VerticalAlignment.Center };
    public static Button MakeButton(string text, Action action)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 9, 14, 9), Background = Brush("#252F3E"), Foreground = Brush("#D2DBE9"), BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 12 };
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3)); border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center); content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); content.SetValue(FrameworkElement.MarginProperty, new Thickness(14, 9, 14, 9)); border.AppendChild(content); template.VisualTree = border; b.Template = template;
        b.MouseEnter += (_, _) => b.Opacity = .75; b.MouseLeave += (_, _) => b.Opacity = 1; b.Click += (_, _) => action(); return b;
    }
    public MixerWindow(bool preview, bool smokeTest = false)
    {
        this.preview = preview;
        Title = "Neon Mix · Console"; Width = 1170; Height = 632; MinWidth = 660; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = Brush("#202428"); Foreground = Brush("#E5E9EC"); FontFamily = new FontFamily("Segoe UI, Malgun Gothic"); Topmost = prefs.AlwaysOnTop;
        var root = new Grid { Margin = new Thickness(16, 14, 16, 12) }; Content = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) }; root.Children.Add(header);
        var controls = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(controls, Dock.Right); header.Children.Add(controls);
        var settings = MakeButton("설정", OpenSettings); settings.Margin = new Thickness(0, 0, 7, 0); controls.Children.Add(settings); controls.Children.Add(MakeButton("숨기기", () => Hide()));
        var brand = new StackPanel { Orientation = Orientation.Horizontal }; header.Children.Add(brand);
        var wordmark = Label("NEON MIX", 19); wordmark.FontWeight = FontWeights.Bold; brand.Children.Add(wordmark);
        var sub = Label(" /  CONSOLE", 11, Brush("#94A5B0")); sub.Margin = new Thickness(13, 3, 0, 0); brand.Children.Add(sub);
        var toolbar = new Border { Background = Brush("#2B3035"), BorderBrush = Brush("#454C52"), BorderThickness = new Thickness(1), Padding = new Thickness(12, 8, 12, 8) }; Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);
        var toolbarDock = new DockPanel(); toolbar.Child = toolbarDock; DockPanel.SetDock(count, Dock.Right); toolbarDock.Children.Add(count);
        toolbarDock.Children.Add(Label(preview ? "●  CONSOLE  /  예시 화면" : "●  CONSOLE  /  앱 자동 연결", 11, Brush("#98C5AD")));
        var rack = new Grid { Background = Brush("#171A1D"), Margin = new Thickness(0, 1, 0, 0) }; Grid.SetRow(rack, 2); root.Children.Add(rack);
        rack.ColumnDefinitions.Add(new ColumnDefinition()); rack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(158) });
        channelScroll.Content = channels; channelScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto; channelScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        channelScroll.PanningMode = PanningMode.HorizontalOnly; rack.Children.Add(channelScroll);
        ConfigureScrolling();
        masterHost.BorderBrush = Brush("#677078"); masterHost.BorderThickness = new Thickness(2, 0, 0, 0); masterHost.Padding = new Thickness(7, 0, 0, 0); masterHost.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(masterHost, 1); rack.Children.Add(masterHost);
        empty.TextWrapping = TextWrapping.Wrap; empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center; empty.Margin = new Thickness(20); empty.IsHitTestVisible = false; rack.Children.Add(empty);
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; Grid.SetRow(footer, 3); root.Children.Add(footer); DockPanel.SetDock(shortcut, Dock.Right); footer.Children.Add(shortcut); footer.Children.Add(status);
        status.FontSize = 10; shortcut.FontSize = 10;
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Neon Mix · 앱별 볼륨", Visible = !preview };
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add("믹서 열기", null, (_, _) => Dispatcher.Invoke(ShowMixer)); menu.Items.Add("종료", null, (_, _) => Dispatcher.Invoke(() => { quitting = true; Close(); })); tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMixer);
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle; source = HwndSource.FromHwnd(handle); source.AddHook(Hook);
            if (!preview && !ConfigureHotkey(Math.Clamp(prefs.Hotkey, 0, 3), false))
                foreach (int alternative in Enumerable.Range(0, 4).Where(i => i != prefs.Hotkey).ToArray()) if (ConfigureHotkey(alternative, false)) break;
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
        Closing += (_, e) => { if (!quitting && !preview) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => { timer.Stop(); meterTimer.Stop(); if (hotkeyRegistered) UnregisterHotKey(handle, 1); source?.RemoveHook(Hook); tray.Dispose(); snapshot.Dispose(); };
        timer.Tick += (_, _) => Refresh();
        meterTimer.Tick += (_, _) => { if (!IsVisible) return; foreach (var c in snapshot.Channels) try { if (cards.TryGetValue(c.Key, out var card)) card.Fader.UpdatePeak(c.Peak); } catch (COMException) { } };
        Loaded += (_, _) =>
        {
            if (preview) { AddPreview(); SaveImage("design-preview.png"); quitting = true; Close(); }
            else
            {
                Refresh(); timer.Start(); meterTimer.Start();
                if (smokeTest)
                {
                    var checkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    checkTimer.Tick += (_, _) =>
                    {
                        checkTimer.Stop(); var checks = new List<string>();
                        SaveImage("live-preview.png"); checks.Add($"PASS: live UI rendered, {cards.Count} channels");
                        double oldWidth = Width; Width = 760; UpdateLayout();
                        var firstApp = cards.FirstOrDefault(c => c.Key != "master").Value;
                        if (firstApp != null && channelScroll.ScrollableWidth > 0)
                        {
                            double before = firstApp.Fader.Value;
                            var masterPosition = masterHost.TranslatePoint(new Point(), this);
                            firstApp.Fader.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                            UpdateLayout();
                            checks.Add(channelScroll.HorizontalOffset > 0 ? "PASS: wheel over fader scrolls channel list" : "FAIL: wheel scroll");
                            checks.Add(before == firstApp.Fader.Value ? "PASS: wheel leaves volume unchanged" : "FAIL: wheel changed volume");
                            checks.Add(masterHost.TranslatePoint(new Point(), this) == masterPosition ? "PASS: master remains fixed during scrolling" : "FAIL: master position changed");
                        }
                        Width = oldWidth; channelScroll.ScrollToHorizontalOffset(0); UpdateLayout();
                        checks.Add(hotkeyRegistered ? "PASS: registered shortcut " + shortcuts[prefs.Hotkey] : "FAIL: no available hotkey");
                        Hide(); ShowMixer(); checks.Add(IsVisible ? "PASS: hide/show and audio refresh" : "FAIL: show failed");
                        Refresh(); Refresh(); checks.Add("PASS: repeated session refresh");
                        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "ui-test.txt"), checks);
                        quitting = true; Close();
                    };
                    checkTimer.Start();
                }
            }
        };
    }
    void ConfigureScrolling()
    {
        Point start = default; double offset = 0; bool dragging = false;
        channelScroll.PreviewMouseWheel += (_, e) => { channelScroll.ScrollToHorizontalOffset(channelScroll.HorizontalOffset - e.Delta); e.Handled = true; };
        channelScroll.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            start = e.GetPosition(this); offset = channelScroll.HorizontalOffset;
            dragging = channelScroll.CaptureMouse(); if (dragging) channelScroll.Cursor = Cursors.ScrollWE; e.Handled = true;
        };
        channelScroll.PreviewMouseMove += (_, e) =>
        {
            if (!dragging) return;
            if (e.MiddleButton != MouseButtonState.Pressed) { channelScroll.ReleaseMouseCapture(); return; }
            channelScroll.ScrollToHorizontalOffset(offset + start.X - e.GetPosition(this).X); e.Handled = true;
        };
        channelScroll.PreviewMouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle && dragging) { channelScroll.ReleaseMouseCapture(); e.Handled = true; } };
        channelScroll.LostMouseCapture += (_, _) => { dragging = false; channelScroll.ClearValue(CursorProperty); };
    }
    void ShowMixer() { Show(); WindowState = WindowState.Normal; Activate(); Refresh(); }
    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == 1) { if (IsVisible && IsActive) Hide(); else ShowMixer(); handled = true; }
        return IntPtr.Zero;
    }
    bool ConfigureHotkey(int index, bool save)
    {
        uint mods = index == 3 ? 5u : 3u; uint key = index switch { 1 => 0x78u, 2 => 0x79u, _ => 0x4Du };
        if (hotkeyRegistered && index == prefs.Hotkey) return true;
        // Trial registration preserves the previous shortcut if a new one is busy.
        if (!RegisterHotKey(handle, 2, mods | 0x4000, key)) { status.Text = "단축키 사용 중 · 설정에서 변경해 주세요"; return false; }
        UnregisterHotKey(handle, 2);
        int previous = prefs.Hotkey; bool hadPrevious = hotkeyRegistered;
        if (hotkeyRegistered) UnregisterHotKey(handle, 1);
        hotkeyRegistered = RegisterHotKey(handle, 1, mods | 0x4000, key);
        if (!hotkeyRegistered) { if (hadPrevious) { uint oldKey = previous switch { 1 => 0x78u, 2 => 0x79u, _ => 0x4Du }; hotkeyRegistered = RegisterHotKey(handle, 1, (previous == 3 ? 5u : 3u) | 0x4000, oldKey); } return false; }
        prefs.Hotkey = index; shortcut.Text = shortcuts[index] + "  ·  Esc 숨기기"; if (save) prefs.Save(); return true;
    }
    void OpenSettings()
    {
        var dialog = new Window { Title = "Neon Mix 설정", Owner = this, Width = 460, Height = 330, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground, Topmost = Topmost };
        var p = new StackPanel { Margin = new Thickness(25) }; dialog.Content = p;
        p.Children.Add(Label("믹서 열기 / 숨기기", 16));
        var combo = new System.Windows.Controls.ComboBox { ItemsSource = shortcuts, SelectedIndex = prefs.Hotkey, Margin = new Thickness(0, 14, 0, 16), Padding = new Thickness(8) }; p.Children.Add(combo);
        var check = new System.Windows.Controls.CheckBox { Content = "항상 다른 창 위에 표시", IsChecked = prefs.AlwaysOnTop, Foreground = Foreground }; p.Children.Add(check);
        var help = Label("Fn 조합은 키보드가 F9/F10을 보내도록 설정한 경우에 사용할 수 있습니다.\n창의 × 버튼과 Esc는 트레이로 숨깁니다. 종료는 트레이 메뉴에서 선택하세요.", 12, Brush("#9CAAC0")); help.TextWrapping = TextWrapping.Wrap; help.Margin = new Thickness(0, 18, 0, 18); p.Children.Add(help);
        p.Children.Add(MakeButton("저장", () => { if (!ConfigureHotkey(combo.SelectedIndex, false)) { System.Windows.MessageBox.Show(dialog, "다른 앱이 사용 중인 단축키입니다. 다른 조합을 선택해 주세요."); return; } prefs.AlwaysOnTop = check.IsChecked == true; Topmost = prefs.AlwaysOnTop; prefs.Save(); dialog.Close(); })); dialog.ShowDialog();
    }
    void Refresh()
    {
        if (cards.Values.Any(c => c.Interacting)) return;
        try
        {
            var next = AudioSnapshot.Read(); var previous = snapshot; snapshot = next; previous.Dispose();
            var keys = next.Channels.Select(c => c.Key).ToHashSet();
            foreach (var key in cards.Keys.Where(k => !keys.Contains(k)).ToArray()) { if (key == "master") masterHost.Child = null; else channels.Children.Remove(cards[key]); cards.Remove(key); }
            foreach (var channel in next.Channels.OrderBy(c => c.Key == "master" ? 0 : 1).ThenBy(c => c.Name))
            {
                if (!cards.TryGetValue(channel.Key, out var card))
                {
                    string key = channel.Key;
                    card = new ChannelCard(channel.Name, key, key == "master" ? Parse("#D2B47C") : palette[cards.Count % palette.Length], v => Change(key, c => c.SetVolume((float)(v / 100))), () => Change(key, c => c.SetMute(!c.Muted)));
                    cards.Add(key, card); if (key == "master") masterHost.Child = card; else channels.Children.Add(card);
                }
                card.Update(channel.Volume * 100, channel.Muted, channel.Detail);
            }
            int appCount = next.Channels.Count(c => c.Key != "master"); count.Text = $"{appCount:00} APP CHANNELS"; empty.Visibility = appCount == 0 ? Visibility.Visible : Visibility.Collapsed;
            status.Text = next.Channels.Count == 0 ? "출력 장치가 없습니다. 헤드셋을 연결해 주세요." : next.Warnings.Count > 0 ? "일부 출력 장치 연결을 확인해 주세요" : !hotkeyRegistered ? "단축키 사용 중 · 설정에서 변경해 주세요" : "휠 / 휠 드래그: 목록 이동   ·   페이더 드래그: 볼륨";
        }
        catch (Exception e) when (e is COMException or InvalidOperationException) { status.Text = "오디오 장치 재연결 중…"; }
    }
    void Change(string key, Action<AudioChannel> action)
    {
        try { var c = snapshot.Channels.FirstOrDefault(c => c.Key == key); if (c == null) return; action(c); cards[key].Update(c.Volume * 100, c.Muted, c.Detail); }
        catch (COMException) { status.Text = "앱 연결이 변경되었습니다. 다시 시도해 주세요."; }
    }
    void AddPreview()
    {
        string[] names = ["전체 출력", "VALORANT", "Discord", "Spotify"]; double[] values = [74, 82, 65, 28];
        for (int i = 0; i < names.Length; i++) { var c = new ChannelCard(names[i], i == 0 ? "master" : names[i], palette[i], _ => { }, () => { }); c.Update(values[i], false, i == 0 ? "기본 출력 장치" : "오디오 연결됨"); if (i == 0) masterHost.Child = c; else channels.Children.Add(c); }
        count.Text = "03 APP CHANNELS"; empty.Visibility = Visibility.Collapsed; status.Text = "휠 / 휠 드래그: 목록 이동   ·   페이더 드래그: 볼륨"; shortcut.Text = "Ctrl + Alt + M  ·  Esc 숨기기";
    }
    void SaveImage(string name)
    {
        UpdateLayout(); var root = (FrameworkElement)Content;
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen()) { dc.DrawRectangle(Background, null, new Rect(0, 0, root.ActualWidth + 32, root.ActualHeight + 26)); dc.DrawRectangle(new VisualBrush(root), null, new Rect(16, 14, root.ActualWidth, root.ActualHeight)); }
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth + 32, (int)root.ActualHeight + 26, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, name)); encoder.Save(stream);
    }
}
