using AdbMirrorStudio.Domain.Mirroring;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace AdbMirrorStudio.App.Mirroring;

internal sealed partial class MirrorToolbarWindow : Window
{
    private static readonly (string Label, string Glyph, int? Key)[] Actions =
    [
        ("返回", "\uE72B", 4), ("主屏", "\uE80F", 3), ("最近应用", "\uE7C4", 187),
        ("电源 / 锁屏", "\uE7E8", 26), ("音量 +", "\uE767", 24), ("音量 -", "\uE992", 25),
        ("截屏并保存", "\uE722", null)
    ];

    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Button _more = CreateButton("设备快捷控制", "\uE712", 34);
    private readonly MenuFlyout _menu = new();
    private MirrorToolbarPlacement? _placement;
    private bool _closed;

    public MirrorToolbarWindow(MirrorSession session, nint owner, Action<MirrorSession, nint, int?> invoke)
    {
        Owner = owner;
        Title = $"{session.DeviceSerial} · 设备快捷控制";
        Closed += (_, _) => _closed = true;
        var root = new Grid
        {
            RequestedTheme = ElementTheme.Dark,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 38, 38, 38))
        };
        _buttons.HorizontalAlignment = HorizontalAlignment.Center;
        _buttons.VerticalAlignment = VerticalAlignment.Center;
        _more.HorizontalAlignment = HorizontalAlignment.Center;
        _more.VerticalAlignment = VerticalAlignment.Center;
        _more.Flyout = _menu;
        root.Children.Add(_buttons);
        root.Children.Add(_more);
        Content = root;

        var handle = WindowNative.GetWindowHandle(this);
        foreach (var (label, glyph, key) in Actions)
        {
            var enabled = key is null || session.Profile?.ReadOnly != true;
            var button = CreateButton(label, glyph, 30);
            button.IsEnabled = enabled;
            button.Click += (_, _) => Run(key);
            _buttons.Children.Add(button);

            var item = new MenuFlyoutItem { Text = label, Icon = CreateIcon(glyph), IsEnabled = enabled };
            item.Click += (_, _) => Run(key);
            _menu.Items.Add(item);
        }

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.PreferredMinimumWidth = 1;
        presenter.PreferredMinimumHeight = 1;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        if (!MirrorWindowNative.Attach(handle, owner)) Close();

        void Run(int? keyCode)
        {
            if (!_closed && MirrorWindowNative.BelongsToProcess(owner, session.ProcessId))
                invoke(session, handle, keyCode);
        }
    }

    public nint Owner { get; }
    public bool IsClosed => _closed;

    public void Follow(MirrorToolbarPlacement? placement)
    {
        if (_closed) return;
        if (placement is not { } bounds)
        {
            if (AppWindow.IsVisible)
            {
                _menu.Hide();
                AppWindow.Hide();
            }
            return;
        }

        if (_placement != bounds)
        {
            _buttons.Visibility = bounds.Compact ? Visibility.Collapsed : Visibility.Visible;
            _more.Visibility = bounds.Compact ? Visibility.Visible : Visibility.Collapsed;
            if (!bounds.Compact) _menu.Hide();
            AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
            _placement = bounds;
        }
        if (!AppWindow.IsVisible) AppWindow.Show(activateWindow: false);
    }

    private static FontIcon CreateIcon(string glyph) => new() { Glyph = glyph, FontSize = 13 };

    private static Button CreateButton(string label, string glyph, int width)
    {
        var button = new Button
        {
            Content = CreateIcon(glyph), Width = width, Height = 23, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        return button;
    }
}
