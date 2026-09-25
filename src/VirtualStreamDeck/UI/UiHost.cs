using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VirtualStreamDeck.Services;
using VirtualStreamDeck.UI.Controls;
using VirtualStreamDeck.UI.Windows;

namespace VirtualStreamDeck.UI;

/// <summary>Small in-app notifications, stacked bottom-right. No Windows toast spam.</summary>
public sealed class ToastHost : StackPanel
{
    public ToastHost()
    {
        VerticalAlignment = VerticalAlignment.Bottom;
        HorizontalAlignment = HorizontalAlignment.Right;
        Margin = new Thickness(0, 0, 20, 20);
        IsHitTestVisible = false;
        MaxWidth = 420;
    }

    public void Show(string text, ToastKind kind)
    {
        var p = ThemeManager.Current;
        var (glyph, color) = kind switch
        {
            ToastKind.Success => ("", p.Success),
            ToastKind.Warning => ("", p.Warning),
            ToastKind.Error => ("", p.Danger),
            _ => ("", p.Accent),
        };
        var row = new DockPanel();
        var icon = new TextBlock { Text = glyph, FontFamily = IconVisual.Glyphs, FontSize = 14, Foreground = ThemeManager.Brush(color), Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = ThemeManager.Brush(p.Text), FontSize = 13 });
        var card = new Border
        {
            Child = row,
            Background = ThemeManager.Brush(p.Raised),
            BorderBrush = ThemeManager.Brush(kind == ToastKind.Error ? p.Danger : p.BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 16, 10),
            Margin = new Thickness(0, 8, 0, 0),
            Opacity = 0,
        };
        Children.Add(card);
        while (Children.Count > 4) Children.RemoveAt(0);
        var anim = ThemeManager.AnimationLevel > 0;
        if (anim) card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        else card.Opacity = 1;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(kind == ToastKind.Error ? 5500 : 2800) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (!anim) { Children.Remove(card); return; }
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) => Children.Remove(card);
            card.BeginAnimation(OpacityProperty, fade);
        };
        t.Start();
    }
}

/// <summary>IUiHost implementation: dialogs owned by the right window, toasts on visible windows.</summary>
public sealed class UiHost : IUiHost
{
    public static UiHost Instance { get; } = new();
    private readonly List<(Window Window, ToastHost Host)> _hosts = new();
    public bool Silent { get; set; } // headless verification: no modal dialogs

    public void Register(Window w, ToastHost host)
    {
        _hosts.Add((w, host));
        w.Closed += (_, _) => _hosts.RemoveAll(h => h.Window == w);
    }

    private static Window? Owner(ActionContext ctx)
    {
        if (ctx.Owner is { IsVisible: true } o) return o;
        return Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
               ?? Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible && w.WindowState != WindowState.Minimized);
    }

    public Task<bool> ConfirmAsync(ActionContext ctx, string title, string message, string confirmLabel, bool danger)
    {
        if (Silent) return Task.FromResult(false);
        return Task.FromResult(DialogWindow.Confirm(Owner(ctx), title, message, confirmLabel, danger));
    }

    public Task<string?> ResolveMissingPathAsync(ActionContext ctx, string title, string message, string path, bool folder)
    {
        if (Silent) return Task.FromResult<string?>(null);
        return Task.FromResult(DialogWindow.MissingPath(Owner(ctx), title, message, path, folder));
    }

    public void Toast(string text, ToastKind kind = ToastKind.Info)
    {
        Core.Log.Info($"toast[{kind}]: {text}");
        var visible = _hosts.Where(h => h.Window.IsVisible && h.Window.WindowState != WindowState.Minimized).ToList();
        foreach (var h in visible) h.Host.Show(text, kind);
    }
}
