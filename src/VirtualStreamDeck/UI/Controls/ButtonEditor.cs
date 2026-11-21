using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Interop;
using VirtualStreamDeck.Services;
using VirtualStreamDeck.UI.Windows;

namespace VirtualStreamDeck.UI.Controls;

/// <summary>
/// Edit Mode side panel. Works on a deep copy of the button; SAVE commits it, CANCEL discards,
/// TEST runs the edited action without saving.
/// </summary>
public sealed class ButtonEditor : DockPanel
{
    private readonly Deck _deck;
    private DeckButton? _original;
    private DeckButton _edit = new();
    private readonly StackPanel _form = new();
    private readonly DeckButtonView _preview = new(new DeckButton()) { Width = 104, Height = 104, IsHitTestVisible = false };
    private readonly TextBlock _dirtyText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private bool _dirty;

    public event Action? Closed;
    public DeckButton? Original => _original;

    public ButtonEditor(Deck deck)
    {
        _deck = deck;
        LastChildFill = true;
        var header = new DockPanel { Margin = new Thickness(18, 16, 18, 8) };
        var close = F.IconButton("", "Fermer l’éditeur (Échap)", () => Close(confirmDiscard: true));
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var t = F.Caps("Mode édition · bouton", 0);
        t.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(t);
        DockPanel.SetDock(header, Dock.Top);
        Children.Add(header);

        var footer = new Border { Padding = new Thickness(18, 12, 18, 14), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        var fg = new Grid();
        fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = F.Buttons(
            F.IconButton("", "Tester l’action (sans enregistrer)", Test),
            F.IconButton("", "Dupliquer le bouton", Duplicate),
            F.IconButton("", "Supprimer le bouton", Delete));
        left.Margin = new Thickness(0);
        fg.Children.Add(left);
        var right = F.Buttons(F.Button("Annuler", () => Close(confirmDiscard: false)), F.Button("ENREGISTRER", Save, "Btn.Accent"));
        right.Margin = new Thickness(0);
        Grid.SetColumn(right, 1);
        fg.Children.Add(right);
        footer.Child = fg;
        DockPanel.SetDock(footer, Dock.Bottom);
        Children.Add(footer);

        var previewRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 4, 18, 6) };
        previewRow.Children.Add(_preview);
        _dirtyText.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
        previewRow.Children.Add(_dirtyText);
        DockPanel.SetDock(previewRow, Dock.Top);
        Children.Add(previewRow);

        var scroll = new ScrollViewer { Content = _form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(18, 0, 14, 12) };
        Children.Add(scroll);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(confirmDiscard: true); }
            else if (e.Key == System.Windows.Input.Key.S && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control) { e.Handled = true; Save(); }
        };
    }

    public void Load(DeckButton b)
    {
        _original = b;
        _edit = Json.Clone(b);
        _dirty = false;
        Build();
    }

    private void Changed(bool rebuild = false)
    {
        _dirty = true;
        _dirtyText.Text = "Modifications non enregistrées";
        _preview.Update(_deck, false, _edit);
        if (rebuild) Build();
    }

    private void Build()
    {
        _form.Children.Clear();
        _preview.Update(_deck, false, _edit);
        _dirtyText.Text = _dirty ? "Modifications non enregistrées" : "";
        var b = _edit;

        _form.Children.Add(F.Caps("Apparence", 6));
        _form.Children.Add(F.Field("Titre", F.Text(b.Title, v => { b.Title = v; Changed(); })));
        _form.Children.Add(F.Field("Sous-titre", F.Text(b.Subtitle, v => { b.Subtitle = v; Changed(); }),
            "Les boutons dynamiques (microphone, volume, OBS…) affichent ici leur état réel à la place."));
        _form.Children.Add(F.Field("Icône", IconRow(b.Icon, ic => { b.Icon = ic; Changed(rebuild: true); })));
        _form.Children.Add(F.Field("Arrière-plan", F.Color(b.Background, v => { b.Background = v; Changed(); })));
        _form.Children.Add(F.Field("Couleur d’accentuation", F.Color(b.Accent, v => { b.Accent = v; Changed(); })));
        _form.Children.Add(F.Field("Couleur du texte", F.Color(b.TextColor, v => { b.TextColor = v; Changed(); })));
        _form.Children.Add(F.Field("Taille", F.Combo(new[] { ("1x1", "1 × 1"), ("2x1", "2 de large"), ("1x2", "2 de haut"), ("2x2", "2 × 2") },
            $"{b.ColSpan}x{b.RowSpan}", v => { b.ColSpan = v[0] - '0'; b.RowSpan = v[2] - '0'; Changed(); }),
            "Les formats plus grands nécessitent des cases libres à droite ou en dessous."));

        _form.Children.Add(F.Caps("Comportement"));
        _form.Children.Add(F.Field("Type de bouton", F.Combo(ButtonModes.All, b.Mode, v =>
        {
            b.Mode = v;
            if (v == ButtonModes.Toggle && b.States.Count == 0) b.States.Add(new ButtonState { Title = b.Title, Subtitle = "DÉSACTIVÉ", Action = new ActionSpec() });
            if (v == ButtonModes.MultiState && b.States.Count == 0) b.States.Add(new ButtonState { Title = b.Title, Subtitle = "ÉTAT 2", Action = new ActionSpec() });
            b.CurrentState = 0;
            Changed(rebuild: true);
        }), b.Mode switch
        {
            ButtonModes.Momentary => "Les raccourcis clavier restent enfoncés tant que vous maintenez le bouton (appuyer pour parler). Les autres actions se déclenchent au clic.",
            ButtonModes.Toggle => "Chaque pression bascule vers l’autre état et exécute l’action correspondante.",
            ButtonModes.MultiState => "Chaque pression passe à l’état suivant et exécute son action, puis revient au premier état.",
            _ => null,
        }));

        _form.Children.Add(F.Field(b.Mode is ButtonModes.Toggle or ButtonModes.MultiState ? "État 1 — action" : "Action",
            new ActionEditor(_deck, b.Action, () => Changed(), () => Changed(rebuild: true))));

        if (b.Mode is ButtonModes.Toggle or ButtonModes.MultiState)
        {
            var count = b.Mode == ButtonModes.Toggle ? Math.Min(1, b.States.Count) : b.States.Count;
            for (var i = 0; i < count; i++)
            {
                var s = b.States[i];
                var idx = i;
                var card = new StackPanel();
                var hdr = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                if (b.Mode == ButtonModes.MultiState && b.States.Count > 1)
                {
                    var rm = F.IconButton("", "Supprimer cet état", () => { b.States.RemoveAt(idx); b.CurrentState = 0; Changed(rebuild: true); });
                    DockPanel.SetDock(rm, Dock.Right);
                    hdr.Children.Add(rm);
                }
                hdr.Children.Add(F.Caps($"État {i + 2}", 6));
                card.Children.Add(hdr);
                card.Children.Add(F.Field("Titre", F.Text(s.Title, v => { s.Title = v; Changed(); })));
                card.Children.Add(F.Field("Sous-titre", F.Text(s.Subtitle, v => { s.Subtitle = v; Changed(); })));
                card.Children.Add(F.Field("Icône (vide = identique à l’état 1)", IconRow(s.Icon ?? new IconSpec(), ic => { s.Icon = ic; Changed(rebuild: true); })));
                card.Children.Add(F.Field("Accentuation", F.Color(s.Accent, v => { s.Accent = v; Changed(); })));
                card.Children.Add(F.Field("Action", new ActionEditor(_deck, s.Action, () => Changed(), () => Changed(rebuild: true))));
                _form.Children.Add(F.Card(card, 14));
            }
            if (b.Mode == ButtonModes.MultiState)
                _form.Children.Add(F.Buttons(F.Button("+ Ajouter un état", () =>
                {
                    b.States.Add(new ButtonState { Title = b.Title, Subtitle = $"ÉTAT {b.States.Count + 2}" });
                    Changed(rebuild: true);
                })));
        }

        _form.Children.Add(F.Caps("Options supplémentaires"));
        var sounds = new List<(string, string)> { ("", "Aucun") };
        sounds.AddRange(_deck.Config.Sounds.Select(s => (s.Id, s.Name)));
        _form.Children.Add(F.Field("Son à la pression", F.Combo(sounds, b.SoundId, v => { b.SoundId = v; Changed(); }), "Le son est diffusé par la sortie du panneau sonore pour confirmer le clic."));
        _form.Children.Add(F.Field("Raccourci global", new ChordBox(b.Hotkey, v => { b.Hotkey = v; Changed(); }, allowBareKeys: false),
            "Permet d’activer ce bouton depuis n’importe quelle application."));
        _form.Children.Add(F.ToggleWithHint("Demander confirmation", b.Confirm, v => { b.Confirm = v; Changed(rebuild: true); },
            "Demande une confirmation avant l’exécution. L’arrêt, le redémarrage, la mise en veille et la déconnexion demandent toujours une confirmation."));
        if (b.Confirm)
            _form.Children.Add(F.Field("Question de confirmation", F.Text(b.ConfirmText, v => { b.ConfirmText = v; Changed(); })));
        _form.Children.Add(F.ToggleWithHint("Afficher l’état en temps réel", b.ShowLiveState, v => { b.ShowLiveState = v; Changed(); },
            "Affiche l’état réel du système (MUET, 64 %, EN DIRECT…) en guise de sous-titre."));
    }

    private FrameworkElement IconRow(IconSpec icon, Action<IconSpec> set)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var chip = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 0) };
        chip.SetResourceReference(Border.BackgroundProperty, "Bg.Raised");
        chip.Child = IconVisual.Create(icon, 20, ThemeManager.Brush(ThemeManager.Current.Text));
        row.Children.Add(chip);
        row.Children.Add(F.Button("Choisir une icône…", () =>
        {
            var picked = IconPickerWindow.Pick(Window.GetWindow(this), icon);
            if (picked != null) set(picked);
        }));
        if (!icon.IsEmpty)
        {
            var clr = F.Button("Retirer", () => set(new IconSpec()), "Btn.Ghost");
            clr.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(clr);
        }
        return row;
    }

    private async void Test()
    {
        var copy = Json.Clone(_edit);
        var (_, _, _, _, action) = Deck.Effective(copy);
        var r = await _deck.RunAsync(action, new ActionContext(ActionSource.Test, Window.GetWindow(this), copy));
        if (r.Ok && !r.Toast && r != ActionResult.Cancelled) UiHost.Instance.Toast("Action exécutée", ToastKind.Success);
    }

    private void Save()
    {
        if (_original == null) return;
        if (_edit.Hotkey.Length > 0 && KeyChord.TryParse(_edit.Hotkey, out var hk))
        {
            var clash = GridLayout.AllButtons(_deck.Config).FirstOrDefault(x => x.Button.Id != _original.Id && KeyChord.Normalize(x.Button.Hotkey) == hk.ToString());
            if (clash.Button != null)
            {
                DialogWindow.Info(Window.GetWindow(this), "Raccourci déjà utilisé", $"{hk} is already assigned to \"{clash.Button.Title}\" in {clash.Profile.Name}. Choisissez une autre combinaison.");
                return;
            }
        }
        if (_deck.CommitButton(_original, _edit))
        {
            var saved = _deck.FindButton(_original.Id);
            _dirty = false;
            UiHost.Instance.Toast("Bouton enregistré", ToastKind.Success);
            var reg = _deck.Hotkeys.Registrations.FirstOrDefault(r => r.Chord == KeyChord.Normalize(_edit.Hotkey) && !r.Ok);
            if (_edit.Hotkey.Length > 0 && reg != null) UiHost.Instance.Toast($"Le raccourci {reg.Chord} n’a pas pu être enregistré : {reg.Error}", ToastKind.Warning);
            if (saved != null) Load(saved);
        }
    }

    private void Duplicate()
    {
        if (_original == null) return;
        var c = _deck.DuplicateButton(_original);
        if (c != null) { Load(c); UiHost.Instance.Toast("Bouton dupliqué", ToastKind.Success); }
    }

    private void Delete()
    {
        if (_original == null) return;
        if (!DialogWindow.Confirm(Window.GetWindow(this), $"Supprimer \"{(_original.Title.Length > 0 ? _original.Title : "ce bouton")}\" ?", "Cette action est irréversible.", "SUPPRIMER", true)) return;
        _deck.DeleteButton(_original);
        _original = null;
        _dirty = false;
        Closed?.Invoke();
    }

    public bool ConfirmLeave()
    {
        if (!_dirty) return true;
        return DialogWindow.Confirm(Window.GetWindow(this), "Abandonner les modifications non enregistrées ?", "Le bouton conservera ses paramètres précédents.", "ABANDONNER", true);
    }

    private void Close(bool confirmDiscard)
    {
        if (confirmDiscard && !ConfirmLeave()) return;
        _dirty = false;
        _original = null;
        Closed?.Invoke();
    }
}

/// <summary>Per-type action fields. Rebuilds itself when the action type changes.</summary>
public sealed class ActionEditor : StackPanel
{
    private readonly Deck _deck;
    private readonly ActionSpec _a;
    private readonly Action _changed;
    private readonly Action _structural;

    public ActionEditor(Deck deck, ActionSpec a, Action changed, Action structural)
    {
        _deck = deck;
        _a = a;
        _changed = changed;
        _structural = structural;
        Build();
    }

    private void Set(Action apply) { apply(); _changed(); }

    private void Build()
    {
        Children.Clear();
        var a = _a;
        Children.Add(F.Combo(ActionTypes.All, a.Type, v =>
        {
            a.Type = v;
            a.Command = v switch
            {
                ActionTypes.Power => PowerCommands.Lock,
                ActionTypes.Media => MediaCmds.PlayPause,
                ActionTypes.System => SysCommands.MicToggle,
                ActionTypes.Deck => DeckCommands.SwitchProfile,
                ActionTypes.Obs => ObsCommands.ToggleStream,
                ActionTypes.Discord => DiscordCommands.Launch,
                _ => "",
            };
            if (v == ActionTypes.Sound && string.IsNullOrEmpty(a.Param)) a.Param = _deck.Config.Sounds.FirstOrDefault()?.Id ?? "";
            if (v == ActionTypes.Deck) a.Param = _deck.Config.Profiles.FirstOrDefault()?.Id ?? "";
            _structural();
        }));
        var body = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        Children.Add(body);
        void Add(UIElement e) => body.Children.Add(e);

        switch (a.Type)
        {
            case ActionTypes.LaunchApp:
                Add(F.Field("Programme", F.Path(a.Target, v => Set(() => a.Target = v), "Programmes|*.exe;*.bat;*.cmd;*.lnk;*.msc|Tous les fichiers|*.*"),
                    "Un chemin complet ou un nom que Windows peut trouver, comme chrome.exe ou notepad.exe."));
                Add(F.Field("Choisir une application en cours d’exécution", RunningApps(v => { a.Target = v; _structural(); })));
                Add(F.Field("Arguments (facultatif)", F.Text(a.Arguments, v => Set(() => a.Arguments = v))));
                Add(F.Field("Démarrer dans (facultatif)", F.Path(a.WorkingDirectory, v => Set(() => a.WorkingDirectory = v), "", folder: true),
                    "Par défaut, le dossier du programme est utilisé."));
                Add(F.Toggle("Placer au premier plan si l’application est déjà ouverte", a.FocusIfRunning, v => Set(() => a.FocusIfRunning = v)));
                Add(F.ToggleWithHint("Exécuter en tant qu’administrateur", a.RunAsAdmin, v => Set(() => a.RunAsAdmin = v), "Windows demande une autorisation (UAC) à chaque fois."));
                break;
            case ActionTypes.OpenUrl:
                Add(F.Field("URL", F.Text(a.Target, v => Set(() => a.Target = v)),
                    "https://youtube.com, http://192.168.1.100 ou des liens d’application comme steam://, spotify:, ms-settings:"));
                break;
            case ActionTypes.OpenPath:
                Add(F.Field("Fichier", F.Path(a.Target, v => Set(() => a.Target = v), "Tous les fichiers|*.*")));
                Add(F.Buttons(F.Button("Choisir plutôt un dossier…", () =>
                {
                    var f = Pickers.Folder(Window.GetWindow(this), a.Target);
                    if (f != null) { a.Target = f; _structural(); }
                })));
                break;
            case ActionTypes.Hotkey:
                Add(F.Field("Touches", new ChordBox(a.Keys, v => Set(() => a.Keys = v)),
                    "Envoyées à l’application que vous utilisiez avant d’appuyer sur le bouton. Exemples : CTRL + MAJ + M, ALT + TAB, F13."));
                break;
            case ActionTypes.Macro:
                Add(new MacroEditor(a.Steps, _changed));
                break;
            case ActionTypes.TypeText:
                Add(F.Field("Texte à saisir", F.Text(a.Text, v => Set(() => a.Text = v), multiline: true), "Les retours à la ligne sont saisis comme des pressions sur ENTRÉE."));
                break;
            case ActionTypes.Sound:
                SoundFields(body);
                break;
            case ActionTypes.Media:
                Add(F.Field("Commande", F.Combo(MediaCmds.All, a.Command, v => { a.Command = v; _structural(); }),
                    "Lecture/pause, suivant et précédent utilisent la session multimédia de Windows (l’application affichée dans le volet du volume) ; les touches multimédias sont utilisées en solution de secours."));
                if (a.Command is MediaCmds.VolumeUp or MediaCmds.VolumeDown)
                    Add(F.Field("Pas (%)", F.Number(a.Value > 0 ? a.Value : _deck.Config.Settings.VolumeStep, 1, 25, v => Set(() => a.Value = v))));
                break;
            case ActionTypes.System:
                SystemFields(body);
                break;
            case ActionTypes.Power:
                Add(F.Field("Commande", F.Combo(PowerCommands.All, a.Command, v => Set(() => a.Command = v)),
                    "La mise en veille, l’hibernation, le redémarrage, l’arrêt et la déconnexion demandent toujours une confirmation."));
                break;
            case ActionTypes.Deck:
                Add(F.Field("Commande", F.Combo(DeckCommands.All, a.Command, v => { a.Command = v; _structural(); })));
                if (a.Command == DeckCommands.SwitchProfile)
                    Add(F.Field("Profil", F.Combo(_deck.Config.Profiles.Select(p => (p.Id, p.Name)), a.Param, v => Set(() => a.Param = v))));
                if (a.Command == DeckCommands.GoToPage)
                    Add(F.Field("Numéro de page", F.Number(Math.Max(1, a.Value), 1, 50, v => Set(() => a.Value = v))));
                break;
            case ActionTypes.Obs:
                ObsFields(body);
                break;
            case ActionTypes.Discord:
                Add(F.Field("Commande", F.Combo(DiscordCommands.All, a.Command, v => { a.Command = v; _structural(); })));
                Add(F.Hint("Discord ne dispose pas d’API locale pour connaître l’état du micro sans application enregistrée dans le cloud. Les commandes muet et sourdine envoient donc le raccourci clavier " +
                           "défini dans Paramètres › Intégrations. Définissez le même raccourci dans Discord (Paramètres utilisateur › Raccourcis clavier) pour qu’il fonctionne lorsque Discord est en arrière-plan. " +
                           "Le bouton ne prétend pas connaître l’état du micro dans Discord."));
                break;
            case ActionTypes.RunCommand:
                var warn = new Border { Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 12), BorderThickness = new Thickness(1) };
                warn.SetResourceReference(Border.BackgroundProperty, "Danger.Soft");
                warn.SetResourceReference(Border.BorderBrushProperty, "Danger");
                warn.Child = new TextBlock
                {
                    Text = "Commande définie par l’utilisateur. Elle s’exécute avec vos autorisations Windows, exactement telle qu’elle est écrite ici. " +
                           "Utilisez uniquement des commandes que vous avez écrites ou auxquelles vous faites confiance : cette application ne télécharge ni n’exécute de scripts distants.",
                    TextWrapping = TextWrapping.Wrap, FontSize = 12,
                };
                Add(warn);
                Add(F.Field("Interpréteur de commandes", F.Combo(new[] { ("powershell", "Windows PowerShell"), ("cmd", "Invite de commandes (cmd)") }, a.Shell, v => Set(() => a.Shell = v))));
                Add(F.Field("Commande / script", F.Text(a.Text, v => Set(() => a.Text = v), multiline: true)));
                Add(F.Field("Démarrer dans (facultatif)", F.Path(a.WorkingDirectory, v => Set(() => a.WorkingDirectory = v), "", folder: true)));
                Add(F.Toggle("Afficher la fenêtre de la console", a.ShowWindow, v => Set(() => a.ShowWindow = v)));
                break;
        }
    }

    private FrameworkElement RunningApps(Action<string> pick)
    {
        var cb = new ComboBox { MaxDropDownHeight = 360 };
        cb.Items.Add(new ComboBoxItem { Content = "Choisir…", Tag = "" });
        cb.SelectedIndex = 0;
        cb.DropDownOpened += (_, _) =>
        {
            if (cb.Items.Count > 1) return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Native.EnumWindows((h, _) =>
            {
                if (!Native.IsWindowVisible(h) || Native.GetWindowTextLength(h) == 0) return true;
                var path = WindowFocus.ProcessPathOfWindow(h);
                if (path == null || path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase) && !path.EndsWith("notepad.exe", StringComparison.OrdinalIgnoreCase)) return true;
                if (path.Equals(AppInfo.ExePath, StringComparison.OrdinalIgnoreCase) || !seen.Add(path)) return true;
                cb.Items.Add(new ComboBoxItem { Content = $"{Path.GetFileNameWithoutExtension(path)}  —  {path}", Tag = path });
                return true;
            }, IntPtr.Zero);
        };
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is ComboBoxItem { Tag: string p } && p.Length > 0) pick(p); };
        return cb;
    }

    private void SoundFields(StackPanel body)
    {
        var a = _a;
        var items = _deck.Config.Sounds.Select(s => (s.Id, s.Name + (s.IsBuiltIn ? "  (built-in)" : ""))).ToList();
        body.Children.Add(F.Field("Son", F.Combo(items, a.Param, v => Set(() => a.Param = v)), "Importez vos propres fichiers WAV / MP3 / OGG dans le panneau Bibliothèque de sons."));
        body.Children.Add(F.Buttons(
            F.Button("▶ Aperçu", () =>
            {
                var s = _deck.Config.Sounds.FirstOrDefault(x => x.Id == a.Param);
                if (s == null) return;
                try { _deck.Soundboard.Play(s, "preview", a.Volume / 100f, false, a.FadeInMs, a.FadeOutMs, "restart"); }
                catch (Exception ex) { UiHost.Instance.Toast(ex.Message, ToastKind.Error); }
            }),
            F.Button("■ Arrêter", () => _deck.Soundboard.Stop("preview", 80))));
        body.Children.Add(F.Field("Volume", F.Slider(0, 100, a.Volume, 1, v => $"{v:0} %", v => Set(() => a.Volume = (int)v))));
        body.Children.Add(F.Toggle("Répéter jusqu’à la prochaine pression", a.Loop, v => Set(() => { a.Loop = v; if (v && a.Retrigger == "") a.Retrigger = "toggle"; })));
        body.Children.Add(F.Field("Fondu d’entrée (ms)", F.Number(a.FadeInMs, 0, 10000, v => Set(() => a.FadeInMs = v))));
        body.Children.Add(F.Field("Fondu de sortie (ms)", F.Number(a.FadeOutMs, 0, 10000, v => Set(() => a.FadeOutMs = v))));
        body.Children.Add(F.Field("En cas de pression pendant la lecture", F.Combo(new[]
        {
            ("", "Utiliser le réglage global"), ("overlap", "Relancer par-dessus (chevauchement)"), ("restart", "Recommencer depuis le début"), ("toggle", "Arrêter le son"),
        }, a.Retrigger, v => Set(() => a.Retrigger = v))));
    }

    private void SystemFields(StackPanel body)
    {
        var a = _a;
        body.Children.Add(F.Field("Commande", F.Combo(SysCommands.All, a.Command, v => { a.Command = v; _structural(); })));
        switch (a.Command)
        {
            case SysCommands.MasterVolumeSet:
            case SysCommands.MicVolumeSet:
            case SysCommands.BrightnessSet:
                body.Children.Add(F.Field("Niveau", F.Slider(0, 100, a.Value, 1, v => $"{v:0} %", v => Set(() => a.Value = (int)v))));
                break;
            case SysCommands.MasterVolumeUp:
            case SysCommands.MasterVolumeDown:
            case SysCommands.MicVolumeUp:
            case SysCommands.MicVolumeDown:
            case SysCommands.BrightnessUp:
            case SysCommands.BrightnessDown:
                body.Children.Add(F.Field("Pas (%)", F.Number(a.Value > 0 ? a.Value : _deck.Config.Settings.VolumeStep, 1, 25, v => Set(() => a.Value = v))));
                break;
            case SysCommands.OutputSet:
            case SysCommands.InputSet:
                var devs = _deck.Audio.Devices(a.Command == SysCommands.OutputSet ? DataFlow.Render : DataFlow.Capture);
                var items = devs.Select(d => (d.Id, d.Name + (d.IsDefault ? "  (par défaut actuellement)" : ""))).ToList();
                if (a.Param.Length > 0 && devs.All(d => d.Id != a.Param)) items.Insert(0, (a.Param, "(appareil déconnecté)"));
                body.Children.Add(F.Field("Appareil", F.Combo(items, a.Param, v => Set(() => a.Param = v)),
                    "Windows ne fournit pas d’API documentée pour cela ; l’application utilise la même interface que le panneau de configuration du son."));
                break;
            case SysCommands.CameraStatus:
                body.Children.Add(F.Hint("Indique si une application utilise actuellement la caméra et si l’accès à celle-ci est bloqué. " +
                                         "Windows ne propose aucune méthode prise en charge pour désactiver une webcam depuis une application. Une pression ouvre donc la page de confidentialité de la caméra."));
                break;
            case SysCommands.CameraDeviceToggle:
                body.Children.Add(F.Hint("Désactive ou réactive la caméra dans le Gestionnaire de périphériques : il s’agit d’une véritable coupure. Une autorisation administrateur (invite UAC) est requise à chaque fois."));
                break;
            case SysCommands.NightLight:
                body.Children.Add(F.Hint("La veilleuse ne dispose pas d’API Windows publique. Cette commande ouvre sa page de paramètres au lieu de prétendre l’activer ou la désactiver."));
                break;
            case SysCommands.WifiToggle:
            case SysCommands.BluetoothToggle:
                body.Children.Add(F.Hint("Utilise l’API radio de Windows (le même commutateur que dans les Paramètres rapides). Nécessite l’option « Autoriser les applications à contrôler les radios des appareils » dans les paramètres de confidentialité."));
                break;
            case SysCommands.MicToggle:
                body.Children.Add(F.Hint("Coupe le son du périphérique d’enregistrement par défaut de Windows (ou de celui choisi dans Paramètres › Audio). L’état est mis à jour instantanément dès qu’il change, quel que soit l’endroit."));
                break;
        }
    }

    private void ObsFields(StackPanel body)
    {
        var a = _a;
        if (!_deck.Config.Settings.ObsEnabled)
            body.Children.Add(F.Hint("L’intégration OBS est désactivée. Activez-la dans Paramètres › Intégrations (OBS 28 ou version ultérieure, avec son serveur WebSocket activé).", 0));
        body.Children.Add(F.Field("Commande", F.Combo(ObsCommands.All, a.Command, v => { a.Command = v; _structural(); })));
        switch (a.Command)
        {
            case ObsCommands.Scene:
                var tb = F.Text(a.Param, v => Set(() => a.Param = v));
                body.Children.Add(F.Field("Nom de la scène", tb));
                body.Children.Add(F.Buttons(F.Button("Charger les scènes depuis OBS", async () =>
                {
                    try
                    {
                        var names = await _deck.Obs.SceneNamesAsync();
                        if (names.Count == 0) { UiHost.Instance.Toast("OBS ne contient aucune scène", ToastKind.Warning); return; }
                        var m = new ContextMenu();
                        foreach (var n in names)
                        {
                            var mi = new MenuItem { Header = n };
                            mi.Click += (_, _) => tb.Text = n;
                            m.Items.Add(mi);
                        }
                        m.PlacementTarget = tb;
                        m.IsOpen = true;
                    }
                    catch (Exception ex) { UiHost.Instance.Toast(ex.Message, ToastKind.Error); }
                })));
                break;
            case ObsCommands.ToggleSource:
                body.Children.Add(F.Field("Nom de la scène", F.Text(a.Param, v => Set(() => a.Param = v))));
                body.Children.Add(F.Field("Nom de la source", F.Text(a.Param2, v => Set(() => a.Param2 = v))));
                break;
            case ObsCommands.ToggleMute:
                body.Children.Add(F.Field("Nom de la source audio", F.Text(a.Param, v => Set(() => a.Param = v)), "ex. : Mic/Aux ou Audio du bureau"));
                break;
        }
    }
}

/// <summary>Visual list of macro steps with reordering.</summary>
public sealed class MacroEditor : StackPanel
{
    private readonly List<MacroStep> _steps;
    private readonly Action _changed;
    private readonly StackPanel _list = new();

    public MacroEditor(List<MacroStep> steps, Action changed)
    {
        _steps = steps;
        _changed = changed;
        Children.Add(_list);
        var add = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void AddBtn(string label, Func<MacroStep> make)
        {
            var b = F.Button(label, () => { _steps.Add(make()); Rebuild(); _changed(); });
            b.Margin = new Thickness(0, 0, 6, 6);
            b.Padding = new Thickness(10, 5, 10, 5);
            add.Children.Add(b);
        }
        AddBtn("+ Touches", () => new MacroStep { Type = MacroStepTypes.Combo });
        AddBtn("+ Texte", () => new MacroStep { Type = MacroStepTypes.Text });
        AddBtn("+ Attente", () => new MacroStep { Type = MacroStepTypes.Delay, Number = 300 });
        AddBtn("+ Clic", () => new MacroStep { Type = MacroStepTypes.Click });
        AddBtn("+ Déplacement", () => new MacroStep { Type = MacroStepTypes.Move });
        AddBtn("+ Lancement", () => new MacroStep { Type = MacroStepTypes.Launch });
        Children.Add(add);
        Children.Add(F.Hint("Les étapes sont exécutées de haut en bas. Les touches sont envoyées à l’application que vous utilisiez. Les positions de la souris sont exprimées en pixels à l’écran et restent dans les limites de celui-ci."));
        Rebuild();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        if (_steps.Count == 0) _list.Children.Add(F.Hint("Aucune étape pour le moment.", 0));
        for (var i = 0; i < _steps.Count; i++) _list.Children.Add(Row(i));
    }

    private FrameworkElement Row(int i)
    {
        var s = _steps[i];
        var card = new StackPanel();
        var head = new DockPanel();
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(Small("", "Monter", () => Swap(i, i - 1), i > 0));
        tools.Children.Add(Small("", "Descendre", () => Swap(i, i + 1), i < _steps.Count - 1));
        tools.Children.Add(Small("", "Supprimer l’étape", () => { _steps.RemoveAt(i); Rebuild(); _changed(); }, true));
        DockPanel.SetDock(tools, Dock.Right);
        head.Children.Add(tools);
        var num = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        num.SetResourceReference(Border.BackgroundProperty, "Accent.Soft");
        num.Child = new TextBlock { Text = (i + 1).ToString(), FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(num);
        var summary = new TextBlock { Text = s.Describe(), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        head.Children.Add(summary);
        card.Children.Add(head);

        void Touch() { summary.Text = s.Describe(); _changed(); }
        var body = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        body.Children.Add(F.Combo(MacroStepTypes.All, s.Type, v => { s.Type = v; Rebuild(); _changed(); }));
        var detail = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        switch (s.Type)
        {
            case MacroStepTypes.Combo:
            case MacroStepTypes.Key:
            case MacroStepTypes.KeyDown:
            case MacroStepTypes.KeyUp:
                detail.Children.Add(new ChordBox(s.Value, v => { s.Value = v; Touch(); }));
                break;
            case MacroStepTypes.Text:
                detail.Children.Add(F.Text(s.Value, v => { s.Value = v; Touch(); }));
                break;
            case MacroStepTypes.Delay:
                detail.Children.Add(F.Number(s.Number, 0, MacroEngine.MaxDelayMs, v => { s.Number = v; Touch(); }));
                detail.Children.Add(F.Hint("Millisecondes (maximum : 60 000)."));
                break;
            case MacroStepTypes.Click:
                detail.Children.Add(F.Combo(new[] { ("left", "Bouton gauche"), ("right", "Bouton droit"), ("middle", "Bouton du milieu") }, s.Button, v => { s.Button = v; Touch(); }));
                detail.Children.Add(F.Toggle("Cliquer à une position précise", s.AtPosition, v => { s.AtPosition = v; Rebuild(); _changed(); }));
                if (s.AtPosition) detail.Children.Add(XY(s, Touch));
                break;
            case MacroStepTypes.Move:
                detail.Children.Add(XY(s, Touch));
                break;
            case MacroStepTypes.Launch:
                detail.Children.Add(F.Path(s.Value, v => { s.Value = v; Touch(); }, "Programmes|*.exe;*.bat;*.cmd;*.lnk|Tous les fichiers|*.*"));
                break;
        }
        body.Children.Add(detail);
        card.Children.Add(body);
        return F.Card(card, 10);
    }

    private FrameworkElement XY(MacroStep s, Action touch)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var x = F.Number(s.X, -32000, 32000, v => { s.X = v; touch(); });
        var y = F.Number(s.Y, -32000, 32000, v => { s.Y = v; touch(); });
        x.Width = y.Width = 80;
        y.Margin = new Thickness(6, 0, 6, 0);
        sp.Children.Add(x);
        sp.Children.Add(y);
        Button? cap = null;
        cap = F.Button("Capturer dans 3 s", async () =>
        {
            cap!.IsEnabled = false;
            for (var n = 3; n > 0; n--) { cap.Content = $"Déplacez la souris… {n}"; await Task.Delay(1000); }
            Native.GetCursorPos(out var p);
            s.X = p.X; s.Y = p.Y;
            x.Text = p.X.ToString(); y.Text = p.Y.ToString();
            cap.Content = "Capturer dans 3 s";
            cap.IsEnabled = true;
            touch();
        }, tip: "Pointez la cible avec la souris ; sa position sera capturée au bout de 3 secondes.");
        sp.Children.Add(cap);
        return sp;
    }

    private static Button Small(string glyph, string tip, Action click, bool enabled)
    {
        var b = F.IconButton(glyph, tip, click);
        b.Width = b.Height = 28;
        b.FontSize = 11;
        b.IsEnabled = enabled;
        return b;
    }

    private void Swap(int a, int b)
    {
        if (b < 0 || b >= _steps.Count) return;
        (_steps[a], _steps[b]) = (_steps[b], _steps[a]);
        Rebuild();
        _changed();
    }
}
