using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using VirtualStreamDeck.UI.Controls;

namespace VirtualStreamDeck.UI.Windows;

/// <summary>A window with the app's own dark title bar (WindowChrome keeps native snap/resize).</summary>
public class ChromeWindow : Window
{
    private readonly Border _root = new();
    private readonly Grid _titleBar = new() { Height = 44 };
    private readonly ContentControl _header = new() { Focusable = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _body = new() { Focusable = false };
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0) };
    private readonly Button _max;

    public static readonly ImageSource? AppIcon = LoadIcon(small: true);
    public static readonly ImageSource? AppIconLarge = LoadIcon(small: false);

    private static ImageSource? LoadIcon(bool small)
    {
        try
        {
            var dec = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/app.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return small ? dec.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 32)).First() : dec.Frames.OrderByDescending(f => f.PixelWidth).First();
        }
        catch { return null; }
    }

    public ChromeWindow(bool resizable = true, bool minMax = true)
    {
        SetResourceReference(BackgroundProperty, "Bg.App");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        SetResourceReference(FontFamilyProperty, "Font.UI");
        FontSize = 13;
        Icon = AppIcon;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 44,
            ResizeBorderThickness = new Thickness(resizable ? 6 : 0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        _root.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        _root.BorderThickness = new Thickness(1);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0) };
        if (AppIcon != null) brand.Children.Add(new Image { Source = AppIcon, Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center });
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        brand.Children.Add(_title);
        _titleBar.Children.Add(brand);
        Grid.SetColumn(_header, 2);
        WindowChrome.SetIsHitTestVisibleInChrome(_header, true);
        _titleBar.Children.Add(_header);

        var chrome = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(chrome, 3);
        var min = ChromeButton("", "Minimize", () => WindowState = WindowState.Minimized);
        _max = ChromeButton("", "Maximize", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        var close = ChromeButton("", "Close", Close);
        close.SetResourceReference(StyleProperty, "Btn.Close");
        if (minMax)
        {
            chrome.Children.Add(min);
            if (resizable) chrome.Children.Add(_max);
        }
        chrome.Children.Add(close);
        _titleBar.Children.Add(chrome);

        grid.Children.Add(_titleBar);
        Grid.SetRow(_body, 1);
        grid.Children.Add(_body);
        _root.Child = grid;
        Content = _root;

        StateChanged += (_, _) =>
        {
            // A maximised WindowChrome window overhangs the screen by the resize border.
            _root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            _max.Content = WindowState == WindowState.Maximized ? "" : "";
        };
    }

    private static Button ChromeButton(string glyph, string tip, Action click)
    {
        var b = new Button { Content = glyph, ToolTip = tip, Height = 44 };
        b.SetResourceReference(StyleProperty, "Btn.Chrome");
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    public string Caption
    {
        get => _title.Text;
        set { _title.Text = value; Title = value; }
    }

    public object? Header
    {
        get => _header.Content;
        set => _header.Content = value;
    }

    public UIElement? Body
    {
        get => _body.Content as UIElement;
        set => _body.Content = value;
    }

    public void HideTitleBar() => _titleBar.Visibility = Visibility.Collapsed;
}

/// <summary>Themed modal dialogs: confirmation, missing path, text prompt.</summary>
public sealed class DialogWindow : ChromeWindow
{
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
    private readonly StackPanel _stack = new() { Margin = new Thickness(26, 6, 26, 24) };
    private readonly bool _touch;
    public string? Result { get; private set; }

    private DialogWindow(Window? owner, string title, string message, bool touch) : base(resizable: false, minMax: false)
    {
        _touch = touch;
        Caption = "Virtual Stream Deck";
        SizeToContent = SizeToContent.Height;
        Width = touch ? 560 : 460;
        ShowInTaskbar = owner == null;
        if (owner is { IsVisible: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
        }
        _stack.Children.Add(new TextBlock
        {
            Text = title, TextWrapping = TextWrapping.Wrap, FontSize = touch ? 20 : 17, FontWeight = FontWeights.SemiBold,
            FontFamily = IconVisual.Display, Margin = new Thickness(0, 0, 0, 8),
        });
        if (message.Length > 0)
        {
            var msg = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = touch ? 15 : 13 };
            msg.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            _stack.Children.Add(msg);
        }
        var outer = new DockPanel();
        DockPanel.SetDock(_buttons, Dock.Bottom);
        _stack.Children.Add(_buttons);
        Body = _stack;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { Result = null; Close(); }
        };
    }

    private Button AddButton(string label, string? result, string style = "", bool isDefault = false)
    {
        var b = new Button { Content = label, MinWidth = _touch ? 140 : 100, Margin = new Thickness(10, 0, 0, 0), IsDefault = isDefault };
        if (_touch) { b.MinHeight = 52; b.FontSize = 15; }
        if (style.Length > 0) b.SetResourceReference(StyleProperty, style);
        b.Click += (_, _) => { Result = result; Close(); };
        _buttons.Children.Add(b);
        return b;
    }

    private void AddDetail(string text)
    {
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
        };
        _stack.Children.Insert(_stack.Children.Count - 1, box);
    }

    private static bool IsTouch(Window? owner) => owner is ControlSurfaceWindow;

    public static bool Confirm(Window? owner, string title, string message, string confirmLabel, bool danger)
    {
        var d = new DialogWindow(owner, title, message, IsTouch(owner));
        d.AddButton("CANCEL", null);
        var ok = d.AddButton(confirmLabel, "ok", danger ? "Btn.Danger" : "Btn.Accent", isDefault: !danger);
        d.Loaded += (_, _) => (danger ? (UIElement)d._buttons.Children[0] : ok).Focus();
        d.ShowDialog();
        return d.Result == "ok";
    }

    public static void Info(Window? owner, string title, string message)
    {
        var d = new DialogWindow(owner, title, message, IsTouch(owner));
        d.AddButton("OK", "ok", "Btn.Accent", isDefault: true);
        d.ShowDialog();
    }

    public static string? MissingPath(Window? owner, string title, string message, string path, bool folder)
    {
        var d = new DialogWindow(owner, title, message, IsTouch(owner));
        d.AddDetail(path);
        d.AddButton("CANCEL", null);
        d.AddButton("CHANGE PATH", "change", "Btn.Accent", isDefault: true);
        d.ShowDialog();
        if (d.Result != "change") return null;
        return folder ? Pickers.Folder(owner, path) : Pickers.File(owner, "Programs|*.exe;*.bat;*.cmd;*.lnk|All files|*.*", path);
    }

    public static string? Prompt(Window? owner, string title, string label, string initial)
    {
        var d = new DialogWindow(owner, title, label, IsTouch(owner));
        var tb = new TextBox { Text = initial, Margin = new Thickness(0, 12, 0, 0) };
        d._stack.Children.Insert(d._stack.Children.Count - 1, tb);
        d.AddButton("CANCEL", null);
        var ok = d.AddButton("OK", "ok", "Btn.Accent", isDefault: true);
        d.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        d.ShowDialog();
        return d.Result == "ok" ? tb.Text.Trim() : null;
    }
}

public static class Pickers
{
    public static string? File(Window? owner, string filter, string? initial = null)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = filter, CheckFileExists = true };
        TrySetInitial(dlg, initial);
        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }

    public static string[] Files(Window? owner, string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = filter, CheckFileExists = true, Multiselect = true };
        return dlg.ShowDialog(owner) == true ? dlg.FileNames : Array.Empty<string>();
    }

    public static string? Folder(Window? owner, string? initial = null)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (!string.IsNullOrEmpty(initial))
        {
            var dir = initial;
            while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
            if (!string.IsNullOrEmpty(dir)) dlg.InitialDirectory = dir;
        }
        return dlg.ShowDialog(owner) == true ? dlg.FolderName : null;
    }

    public static string? Save(Window? owner, string filter, string fileName)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = fileName, AddExtension = true };
        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }

    private static void TrySetInitial(Microsoft.Win32.FileDialog dlg, string? initial)
    {
        if (string.IsNullOrEmpty(initial)) return;
        var dir = Path.GetDirectoryName(initial);
        while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
        if (!string.IsNullOrEmpty(dir)) dlg.InitialDirectory = dir;
        dlg.FileName = Path.GetFileName(initial);
    }
}
