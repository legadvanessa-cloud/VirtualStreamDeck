using System.Windows.Threading;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Services;
using VirtualStreamDeck.UI.Controls;

namespace VirtualStreamDeck.UI;

/// <summary>Connects the tablet remote server to the deck; everything runs on the UI thread.</summary>
public sealed class RemoteBridge : IRemoteHost
{
    private readonly Deck _deck;
    private readonly Dispatcher _ui;
    private readonly Dictionary<string, (string Version, byte[] Png)> _icons = new();

    public RemoteBridge(Deck deck, Dispatcher ui)
    {
        _deck = deck;
        _ui = ui;
    }

    private string Version(DeckButton b)
    {
        var live = LiveState.Resolve(_deck, b);
        var (t, s, i, a, _) = Deck.Effective(b);
        var key = $"{t}|{s}|{i.Kind}{i.Value}{i.ImageData?.Length}|{a}|{b.Background}|{b.TextColor}|{b.RowSpan}x{b.ColSpan}|{live.State}|{live.Tone}|{live.Icon?.Value}|{ThemeManager.Current.Accent}|{ThemeManager.Current.Key}|{ThemeManager.CornerRadius}";
        return ((uint)key.GetHashCode()).ToString("x8");
    }

    public Task<string> LayoutJsonAsync() => _ui.InvokeAsync(() =>
    {
        var p = _deck.ActiveProfile;
        var page = _deck.ActivePage;
        var obj = new
        {
            profileId = p.Id,
            profiles = _deck.Config.Profiles.Select(x => new { id = x.Id, name = x.Name }),
            page = _deck.PageIndex,
            pageCount = p.Pages.Count,
            cols = p.Columns,
            rows = p.Rows,
            buttons = page.Buttons.Select(b =>
            {
                var (title, sub, _, _, action) = Deck.Effective(b);
                var destructive = action.Type == ActionTypes.Power && PowerCommands.IsDestructive(action.Command);
                return new
                {
                    id = b.Id, row = b.Row, col = b.Col, rs = b.RowSpan, cs = b.ColSpan, title, subtitle = sub,
                    confirm = b.Confirm || destructive,
                    confirmText = destructive ? $"{PowerCommands.All.First(c => c.Id == action.Command).Label} this PC?" : b.ConfirmText,
                    v = Version(b),
                };
            }),
        };
        return Json.Serialize(obj);
    }).Task;

    public Task<byte[]?> IconPngAsync(string buttonId) => _ui.InvokeAsync(() =>
    {
        var b = _deck.FindButton(buttonId);
        if (b == null) return null;
        var v = Version(b);
        if (_icons.TryGetValue(buttonId, out var hit) && hit.Version == v) return hit.Png;
        var png = DeckButtonView.RenderPng(_deck, b, 200 * b.ColSpan + 20 * (b.ColSpan - 1), 200 * b.RowSpan + 20 * (b.RowSpan - 1));
        if (_icons.Count > 300) _icons.Clear();
        _icons[buttonId] = (v, png);
        return (byte[]?)png;
    }).Task;

    public async Task<(bool Ok, string Message)> PressAsync(string buttonId)
    {
        return await await _ui.InvokeAsync(async () =>
        {
            var b = _deck.FindButton(buttonId);
            if (b == null) return (false, "That button no longer exists.");
            var ctx = new ActionContext(ActionSource.Remote) { PreConfirmed = true };
            var r = await _deck.PressAsync(b, ctx);
            await _deck.ReleaseAsync(b, ctx);
            return (r.Ok, r.Toast || !r.Ok ? r.Message ?? "" : "");
        });
    }

    public Task SwitchPageAsync(int page) => _ui.InvokeAsync(() => _deck.SetPage(page)).Task;

    public Task SwitchProfileAsync(string profileId) => _ui.InvokeAsync(() =>
    {
        var p = _deck.Config.Profiles.FirstOrDefault(x => x.Id == profileId);
        if (p != null) _deck.SwitchProfile(p, announce: false);
    }).Task;
}
