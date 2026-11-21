using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Interop;
using VirtualStreamDeck.Services;
using VirtualStreamDeck.UI.Controls;

namespace VirtualStreamDeck.UI.Windows;

/// <summary>Paramètres. Chaque modification est appliquée immédiatement et enregistrée.</summary>
public sealed class SettingsWindow : ChromeWindow
{
    public static readonly string[] Sections =
        { "Général", "Apparence", "Interface de contrôle", "Audio", "Micro de diffusion", "Profils", "Raccourcis clavier", "Intégrations", "Commande à distance sur tablette", "Avancé" };

    private readonly Deck _deck;
    private readonly ListBox _nav = new() { Width = 210, Padding = new Thickness(10, 8, 10, 8) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(32, 18, 32, 28) };
    private readonly ToastHost _toasts = new();
    private AppSettings S => _deck.Config.Settings;
    public string CurrentSection { get; private set; } = "Général";

    public SettingsWindow(Deck deck, Window? owner, string section = "Général")
    {
        _deck = deck;
        Caption = "Paramètres";
        Width = 1000;
        Height = 720;
        MinWidth = 820;
        MinHeight = 520;
        if (owner is { IsVisible: true }) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UiHost.Instance.Register(this, _toasts);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var navBorder = new Border { Child = _nav, BorderThickness = new Thickness(0, 0, 1, 0) };
        navBorder.SetResourceReference(Border.BackgroundProperty, "Bg.Panel");
        navBorder.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        grid.Children.Add(navBorder);
        Grid.SetColumn(_scroll, 1);
        grid.Children.Add(_scroll);
        var overlay = new Grid();
        overlay.Children.Add(grid);
        overlay.Children.Add(_toasts);
        Body = overlay;

        foreach (var s in Sections) _nav.Items.Add(new ListBoxItem { Content = s, Tag = s, Padding = new Thickness(12, 10, 12, 10) });
        _nav.SelectionChanged += (_, _) => { if (_nav.SelectedItem is ListBoxItem { Tag: string t }) ShowSection(t); };
        _nav.SelectedIndex = Math.Max(0, Array.IndexOf(Sections, section));
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    private void Apply(bool theme = false)
    {
        if (theme) ThemeManager.Apply(S);
        _deck.SaveSoon();
        _deck.ApplySettings();
        if (theme) _deck.RequestRelayout();
    }

    public void ShowSection(string name)
    {
        CurrentSection = name;
        foreach (ListBoxItem it in _nav.Items)
            if ((string)it.Tag == name && !it.IsSelected) { it.IsSelected = true; return; } // selection change re-enters here
        var sp =new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        sp.Children.Add(F.Heading(name, 24));
        var body = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        sp.Children.Add(body);
        switch (name)
        {
            case "Général": General(body); break;
            case "Apparence": Appearance(body); break;
            case "Interface de contrôle": Surface(body); break;
            case "Audio": Audio(body); break;
            case "Micro de diffusion": Mic(body); break;
            case "Profils": Profiles(body); break;
            case "Raccourcis clavier": Hotkeys(body); break;
            case "Intégrations": Integrations(body); break;
            case "Commande à distance sur tablette": Remote(body); break;
            case "Avancé": Advanced(body); break;
        }
        _scroll.Content = sp;
        _scroll.ScrollToTop();
    }

    private static void Add(Panel p, params UIElement[] els) { foreach (var e in els) p.Children.Add(e); }

    // ---- sections ---------------------------------------------------------------------------

    private void General(StackPanel b)
    {
        Add(b, F.Caps("Démarrage", 8));
        Add(b, F.ToggleWithHint("Démarrer avec Windows", S.StartWithWindows, v => { S.StartWithWindows = v; Apply(); },
            $"Ajoute {System.IO.Path.GetFileName(AppInfo.ExePath)} à la liste des applications lancées au démarrage de votre session (sans droits administrateur). Si vous déplacez le fichier .exe, désactivez puis réactivez cette option."));
        Add(b, F.Toggle("Démarrer en mode réduit", S.StartMinimized, v => { S.StartMinimized = v; Apply(); }));
        Add(b, F.Caps("Fenêtre"));
        Add(b, F.Toggle("Réduire dans la zone de notification", S.MinimizeToTray, v => { S.MinimizeToTray = v; Apply(); }));
        Add(b, F.ToggleWithHint("Le bouton Fermer laisse l’application active dans la zone de notification", S.CloseToTray, v => { S.CloseToTray = v; Apply(); },
            "Les raccourcis globaux et l’interface de contrôle continuent de fonctionner. Utilisez Quitter dans le menu de la zone de notification pour fermer l’application."));
        Add(b, F.Toggle("Mémoriser la position de la fenêtre", S.RememberWindowPosition, v => { S.RememberWindowPosition = v; Apply(); }));
        Add(b, F.Caps("Notifications"));
        Add(b, F.ToggleWithHint("Afficher les confirmations (✓ Micro coupé, profil modifié…)", S.ShowToasts, v => { S.ShowToasts = v; Apply(); },
            "Les erreurs sont toujours affichées. Aucune notification n’est envoyée au centre de notifications de Windows."));
        Add(b, F.Caps("Langue"));
        var lang = F.Combo(new[] { ("en", "Anglais") }, S.Language, _ => { });
        lang.IsEnabled = false;
        Add(b, F.Field("Langue", lang, "Cette version est disponible uniquement en anglais."));
    }

    private void Appearance(StackPanel b)
    {
        Add(b, F.Field("Thème", F.Combo(ThemeManager.Themes, S.Theme, v => { S.Theme = v; Apply(theme: true); })));
        Add(b, F.Field("Couleur d’accentuation", F.Color(S.AccentColor, v => { if (v != null) { S.AccentColor = v; Apply(theme: true); } }, allowTheme: false)));
        Add(b, F.Caps("Boutons"));
        var p = _deck.ActiveProfile;
        Add(b, F.Field($"Taille de la grille — {p.Name}", F.Combo(GridLayout.Presets.Select(x => ($"{x.Cols}x{x.Rows}", $"{x.Cols} × {x.Rows}")), $"{p.Columns}x{p.Rows}", v =>
        {
            var parts = v.Split('x');
            p.Columns = int.Parse(parts[0]);
            p.Rows = int.Parse(parts[1]);
            _deck.ProfileEdited(p);
        }), "S’applique au profil actif. Les boutons qui ne tiennent plus sont déplacés vers un emplacement libre ou une nouvelle page : aucun n’est supprimé."));
        Add(b, F.Field("Grille par défaut pour les nouveaux profils", F.Combo(GridLayout.Presets.Select(x => ($"{x.Cols}x{x.Rows}", $"{x.Cols} × {x.Rows}")), $"{S.DefaultColumns}x{S.DefaultRows}", v =>
        {
            var parts = v.Split('x');
            S.DefaultColumns = int.Parse(parts[0]);
            S.DefaultRows = int.Parse(parts[1]);
            Apply();
        })));
        Add(b, F.Field("Taille des boutons", F.Combo(new[] { ("fill", "Remplir l’écran"), ("large", "Grand (max. 210 px)"), ("medium", "Moyen (max. 150 px)"), ("small", "Petit (max. 110 px)") }, S.ButtonSize, v => { S.ButtonSize = v; Apply(theme: true); })));
        Add(b, F.Toggle("Boutons carrés", S.SquareButtons, v => { S.SquareButtons = v; Apply(theme: true); }));
        Add(b, F.Field("Rayon des coins", F.Slider(0, 30, S.CornerRadius, 1, v => $"{v:0} px", v => { S.CornerRadius = v; Apply(theme: true); })));
        Add(b, F.Field("Taille de la police", F.Slider(0.8, 1.4, S.FontScale, 0.05, v => $"{v * 100:0} %", v => { S.FontScale = v; Apply(theme: true); })));
        Add(b, F.Field("Animation", F.Combo(new[] { ("0", "Désactivée"), ("1", "Discrète"), ("2", "Complète") }, S.AnimationLevel.ToString(), v => { S.AnimationLevel = int.Parse(v); Apply(theme: true); })));
        Add(b, F.ToggleWithHint("Mode compact", S.CompactMode, v => { S.CompactMode = v; Apply(theme: true); }, "Réduit l’espacement entre les boutons. La barre latérale sera moins large à la prochaine ouverture de la fenêtre principale."));
        Add(b, F.ToggleWithHint("Déclencher au relâchement", S.TriggerOnRelease, v => { S.TriggerOnRelease = v; Apply(); },
            "Désactivé : les boutons se déclenchent dès qu’on appuie dessus (comme sur un contrôleur physique). Activé : ils se déclenchent au relâchement ; si vous faites glisser le doigt hors du bouton, l’action est annulée."));
    }

    private void Surface(StackPanel b)
    {
        var displays = Displays.All();
        var items = new List<(string, string)> { ("", "Automatique (premier écran secondaire)") };
        items.AddRange(displays.Select(d => (d.DeviceName, d.Label)));
        if (S.SurfaceDisplay.Length > 0 && displays.All(d => d.DeviceName != S.SurfaceDisplay)) items.Add((S.SurfaceDisplay, $"{S.SurfaceDisplay} (not connected)"));
        Add(b, F.Field("Écran", F.Combo(items, S.SurfaceDisplay, v => { S.SurfaceDisplay = v; Apply(); }), "Utilisé lorsque vous appuyez sur INTERFACE ou sur le raccourci Ctrl+Alt+S."));
        Add(b, F.Buttons(displays.Select(d => (UIElement)F.Button($"Ouvrir sur l’écran {d.Number}", () => AppShell.OpenSurfaceOn(d))).ToArray()));
        Add(b, F.Toggle("Mémoriser l’écran utilisé à l’ouverture", S.RememberDisplay, v => { S.RememberDisplay = v; Apply(); }));
        Add(b, F.Toggle("Plein écran (sans bordure de fenêtre)", S.SurfaceFullscreen, v => { S.SurfaceFullscreen = v; Apply(); }));
        Add(b, F.Toggle("Toujours au premier plan", S.SurfaceTopmost, v => { S.SurfaceTopmost = v; Apply(); }));
        Add(b, F.Toggle("Ouvrir l’interface de contrôle au démarrage de l’application", S.SurfaceOpenOnStart, v => { S.SurfaceOpenOnStart = v; Apply(); }));
        Add(b, F.Caps("Tactile"));
        Add(b, F.ToggleWithHint("Mode tactile", S.TouchMode, v => { S.TouchMode = v; if (v) { S.SurfaceAutoHideCursor = true; S.ButtonSize = "fill"; } Apply(theme: true); },
            "Les boutons remplissent l’écran et le curseur se masque automatiquement. L’écran tactile fonctionne dans tous les cas ; ce mode adapte simplement l’interface à son utilisation."));
        Add(b, F.ToggleWithHint("Ne pas prendre le focus", S.SurfaceNoActivate, v => { S.SurfaceNoActivate = v; Apply(); },
            "L’utilisation de l’interface ne lui donne pas le focus : votre application actuelle le conserve et continue de recevoir les raccourcis clavier. Ce réglage prendra effet à la prochaine ouverture de l’interface."));
        Add(b, F.Toggle("Masquer automatiquement le curseur de la souris", S.SurfaceAutoHideCursor, v => { S.SurfaceAutoHideCursor = v; Apply(); }));
        Add(b, F.Field("Masquer après (secondes)", F.Number(S.SurfaceCursorHideSeconds, 1, 60, v => { S.SurfaceCursorHideSeconds = v; Apply(); })));
        Add(b, F.Toggle("Empêcher la mise en veille de l’écran lorsque l’interface est ouverte", S.SurfacePreventSleep, v => { S.SurfacePreventSleep = v; Apply(); }));
        Add(b, F.Toggle("Afficher la barre d’état sur l’interface", S.SurfaceShowStatus, v => { S.SurfaceShowStatus = v; Apply(); }));
        Add(b, F.ToggleWithHint("Afficher la console de diffusion sur l’interface", S.SurfaceShowBroadcast, v => { S.SurfaceShowBroadcast = v; Apply(); },
            "Ajoute, à côté des boutons, l’indicateur de niveau du micro, son curseur de volume et les commandes EN DIRECT / MUET. Ce réglage prendra effet à la prochaine ouverture de l’interface."));
        Add(b, F.Hint("Pour fermer l’interface, maintenez quelques instants la croix ✕ située dans son coin supérieur droit : un simple appui ne la ferme jamais.", 8));
    }

    private void Audio(StackPanel b)
    {
        var outs = new List<(string, string)> { ("", "Sortie par défaut de Windows") };
        outs.AddRange(SoundboardEngine.OutputDevices().Select(d => (d.Id, d.Name)));
        Add(b, F.Field("Sortie audio du panneau de sons", F.Combo(outs, S.SoundboardDevice, v => { S.SoundboardDevice = v; Apply(); }),
            "Les sons sont diffusés uniquement sur ce périphérique. Windows ne peut pas envoyer nativement le son d’une application vers plusieurs sorties ; pour transmettre les sons à OBS ou Discord, " +
            "sélectionnez ici un câble audio virtuel (VB-CABLE, VoiceMeeter…) et utilisez-le comme périphérique d’entrée dans ces applications."));
        Add(b, F.Field("Volume du panneau de sons", F.Slider(0, 100, S.SoundboardVolume, 1, v => $"{v:0} %", v => { S.SoundboardVolume = (int)v; Apply(); })));
        Add(b, F.Field("Fondu par défaut (ms)", F.Number(S.DefaultFadeMs, 0, 10000, v => { S.DefaultFadeMs = v; Apply(); }), "Utilisé par la commande Arrêter tous les sons."));
        Add(b, F.Field("Lorsqu’on appuie à nouveau sur un son en cours de lecture", F.Combo(new[] { ("overlap", "Le relancer par-dessus (superposition)"), ("restart", "Le redémarrer"), ("toggle", "L’arrêter") }, S.SoundOverlap, v => { S.SoundOverlap = v; Apply(); })));
        Add(b, F.Caps("Touches de volume"));
        Add(b, F.Field("Incrément du volume (%)", F.Number(S.VolumeStep, 1, 25, v => { S.VolumeStep = v; Apply(); }), "Détermine également l’incrément du curseur de volume du micro avec la molette de la souris et les touches fléchées."));
    }

    private void Mic(StackPanel b)
    {
        var ins = new List<(string, string)> { ("", "Périphérique d’enregistrement par défaut de Windows") };
        ins.AddRange(_deck.Audio.Devices(DataFlow.Capture).Select(d => (d.Id, d.Name + (d.IsDefault ? "  (périphérique par défaut actuel)" : ""))));
        if (S.MicrophoneDevice.Length > 0 && ins.All(i => i.Item1 != S.MicrophoneDevice)) ins.Add((S.MicrophoneDevice, "(microphone déconnecté)"));
        Add(b, F.Field("Microphone contrôlé", F.Combo(ins, S.MicrophoneDevice, v => { S.MicrophoneDevice = v; Apply(); }),
            "Le bouton MICRO, l’indicateur de niveau et le curseur de volume agissent sur ce périphérique. \"Par défaut de Windows\" suit le périphérique utilisé par Windows, y compris les nouveaux appareils branchés."));
        Add(b, F.ToggleWithHint("Restaurer le volume de mon microphone lorsqu’il se (re)connecte", S.MicRestoreVolume, v => { S.MicRestoreVolume = v; Apply(); },
            "Mémorise le dernier volume défini ici pour chaque microphone et le réapplique au démarrage et lors de sa reconnexion."));
        Add(b, F.ToggleWithHint("Afficher les indicateurs de niveau sur les boutons du microphone", S.MeterOnButtons, v => { S.MeterOnButtons = v; Apply(); },
            "Affiche le niveau réel du signal d’entrée sur les boutons MICRO. Cela maintient un flux de capture ouvert ; Windows indique donc que le microphone est utilisé tant que l’application fonctionne."));
        Add(b, F.Caps("Retour audio"));
        var outs = new List<(string, string)> { ("", "Périphérique par défaut de Windows (communications)") };
        outs.AddRange(SoundboardEngine.OutputDevices().Select(d => (d.Id, d.Name)));
        Add(b, F.Field("Sortie du retour audio", F.Combo(outs, S.MicMonitorDevice, v => { S.MicMonitorDevice = v; Apply(); if (_deck.Meter.MonitorEnabled) _deck.Meter.SetMonitor(true, v); }),
            "Périphérique sur lequel vous vous entendez lorsque le RETOUR AUDIO est activé. Utilisez un casque : les haut-parleurs pourraient provoquer un larsen dans le microphone. Prévoyez un délai d’environ 40 à 60 ms (mode audio partagé de Windows)."));
        Add(b, F.Field("Volume du retour audio", F.Slider(0, 100, S.MicMonitorVolume, 1, v => $"{v:0} %", v => { S.MicMonitorVolume = (int)v; _deck.Meter.MonitorVolume = (float)v / 100f; _deck.SaveSoon(); })));
        Add(b, F.Caps("Console en direct"));
        Add(b, new MicModuleView(_deck, compact: true));
    }

    private void Profiles(StackPanel b)
    {
        Add(b, F.ToggleWithHint("Changer automatiquement de profil selon l’application active", S.AutoProfile, v => _deck.SetAutoProfile(v),
            "Surveille la fenêtre au premier plan (déclenchement par événement, sans interrogation périodique) et change de profil selon les règles ci-dessous."));
        var fb = new List<(string, string)> { ("", "Conserver le profil actuel") };
        fb.AddRange(_deck.Config.Profiles.Select(p => (p.Id, p.Name)));
        Add(b, F.Field("Lorsqu’aucune règle ne correspond", F.Combo(fb, S.AutoProfileFallback, v => { S.AutoProfileFallback = v; Apply(); })));
        Add(b, F.Caps("Règles"));
        var list = new StackPanel();
        void Rebuild()
        {
            list.Children.Clear();
            foreach (var r in _deck.Config.AutoProfileRules.ToList())
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var en = new CheckBox { IsChecked = r.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Règle activée" };
                en.Checked += (_, _) => { r.Enabled = true; Apply(); };
                en.Unchecked += (_, _) => { r.Enabled = false; Apply(); };
                g.Children.Add(en);
                var proc = F.Text(r.Process, v => { r.Process = v.Trim(); _deck.SaveSoon(); });
                proc.ToolTip = "Nom du processus, par ex. obs64.exe";
                Grid.SetColumn(proc, 1);
                g.Children.Add(proc);
                var arrow = new TextBlock { Text = "→", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(arrow, 2);
                g.Children.Add(arrow);
                var prof = F.Combo(_deck.Config.Profiles.Select(p => (p.Id, p.Name)), r.ProfileId, v => { r.ProfileId = v; _deck.SaveSoon(); });
                Grid.SetColumn(prof, 3);
                g.Children.Add(prof);
                var del = F.IconButton("", "Supprimer la règle", () => { _deck.Config.AutoProfileRules.Remove(r); _deck.SaveSoon(); Rebuild(); });
                Grid.SetColumn(del, 4);
                g.Children.Add(del);
                list.Children.Add(g);
            }
            if (_deck.Config.AutoProfileRules.Count == 0) list.Children.Add(F.Hint("Aucune règle pour le moment.", 0));
        }
        Rebuild();
        Add(b, list);
        var running = new ComboBox { MinWidth = 280 };
        running.Items.Add(new ComboBoxItem { Content = "Ajouter une règle pour une application en cours d’exécution…", Tag = "" });
        running.SelectedIndex = 0;
        running.DropDownOpened += (_, _) =>
        {
            if (running.Items.Count > 1) return;
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            Native.EnumWindows((h, _) =>
            {
                if (!Native.IsWindowVisible(h) || Native.GetWindowTextLength(h) == 0) return true;
                var path = WindowFocus.ProcessPathOfWindow(h);
                if (path != null && !path.Equals(AppInfo.ExePath, StringComparison.OrdinalIgnoreCase)) names.Add(System.IO.Path.GetFileName(path));
                return true;
            }, IntPtr.Zero);
            foreach (var n in names) running.Items.Add(new ComboBoxItem { Content = n, Tag = n });
        };
        running.SelectionChanged += (_, _) =>
        {
            if (running.SelectedItem is not ComboBoxItem { Tag: string n } || n.Length == 0) return;
            _deck.Config.AutoProfileRules.Add(new AutoProfileRule { Process = n, ProfileId = _deck.ActiveProfile.Id });
            _deck.SaveSoon();
            Rebuild();
            running.SelectedIndex = 0;
        };
        Add(b, F.Buttons(F.Button("+ Ajouter une règle", () =>
        {
            _deck.Config.AutoProfileRules.Add(new AutoProfileRule { Process = "", ProfileId = _deck.ActiveProfile.Id });
            _deck.SaveSoon();
            Rebuild();
        }), running));
        Add(b, F.Hint($"Au premier plan actuellement : {(_deck.Foreground.LastExternalProcess.Length > 0 ? _deck.Foreground.LastExternalProcess : "inconnu")}"));
    }

    private void Hotkeys(StackPanel b)
    {
        Add(b, F.Hint("Les raccourcis globaux fonctionnent depuis n’importe quelle application. Une combinaison déjà utilisée par un autre programme ne peut pas être enregistrée : elle est signalée ci-dessous, tandis que les autres continuent de fonctionner.", 0));
        var list = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var commands = new List<(string, string)>(GlobalCommands.Fixed);
        commands.AddRange(_deck.Config.Profiles.Select(p => (GlobalCommands.ProfilePrefix + p.Id, "Profil : " + p.Name)));
        void Rebuild()
        {
            list.Children.Clear();
            foreach (var h in _deck.Config.Hotkeys.ToList())
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var chord = new ChordBox(h.Chord, v => { h.Chord = v; Apply(); Dispatcher.BeginInvoke(Rebuild); }, allowBareKeys: false);
                g.Children.Add(chord);
                var cmd = F.Combo(commands, h.Command, v => { h.Command = v; Apply(); });
                cmd.Margin = new Thickness(10, 0, 0, 0);
                Grid.SetColumn(cmd, 1);
                g.Children.Add(cmd);
                var del = F.IconButton("", "Supprimer", () => { _deck.Config.Hotkeys.Remove(h); Apply(); Rebuild(); });
                Grid.SetColumn(del, 2);
                g.Children.Add(del);
                list.Children.Add(g);
                var reg = _deck.Hotkeys.Registrations.FirstOrDefault(r => r.Chord == KeyChord.Normalize(h.Chord) && r.Label == _deck.DescribeCommand(h.Command));
                var status = reg == null ? (h.Chord.Length == 0 ? "Non défini" : "Non enregistré") : reg.Ok ? "✓ Enregistré" : "⚠ " + reg.Error;
                var st = F.Hint(status, 0);
                st.Margin = new Thickness(2, 0, 0, 10);
                if (reg is { Ok: false }) st.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
                list.Children.Add(st);
            }
        }
        Rebuild();
        Add(b, list);
        Add(b, F.Buttons(F.Button("+ Ajouter un raccourci", () =>
        {
            _deck.Config.Hotkeys.Add(new HotkeyBinding { Chord = "", Command = GlobalCommands.ToggleMic });
            _deck.SaveSoon();
            Rebuild();
        })));
        var perButton = _deck.Hotkeys.Registrations.Where(r => r.Label.StartsWith("Button:") || r.Label.StartsWith("Profile:") && _deck.Config.Profiles.Any(p => p.Hotkey == r.Chord)).ToList();
        if (perButton.Count > 0)
        {
            Add(b, F.Caps("Définis sur les boutons et les profils"));
            foreach (var r in perButton) Add(b, F.Hint($"{r.Chord}  →  {r.Label}{(r.Ok ? "" : "   ⚠ " + r.Error)}", 2));
        }
        Add(b, F.Hint("Raccourcis dans l’application : Ctrl+K rechercher · Ctrl+E mode édition · Ctrl+1…9 profils · Page précédente/Page suivante pages · flèches + Entrée pour activer les boutons · F11 interface de contrôle.", 16));
    }

    private void Integrations(StackPanel b)
    {
        Add(b, F.Caps("OBS Studio", 8));
        var status = F.Hint(_deck.Obs.StatusText, 0);
        Add(b, F.ToggleWithHint("Activer l’intégration OBS", S.ObsEnabled, v => { S.ObsEnabled = v; Apply(); },
            "Utilise le serveur WebSocket intégré à OBS (OBS 28 et versions ultérieures : Outils › Paramètres du serveur WebSocket). Cette intégration est facultative : le contrôleur fonctionne normalement sans OBS."));
        Add(b, F.Field("Hôte", F.Text(S.ObsHost, v => { S.ObsHost = v.Trim(); _deck.SaveSoon(); })));
        Add(b, F.Field("Port", F.Number(S.ObsPort, 1, 65535, v => { S.ObsPort = v; _deck.SaveSoon(); })));
        var pw = new PasswordBox { Password = Secret.Unprotect(S.ObsPasswordProtected) };
        pw.PasswordChanged += (_, _) => { S.ObsPasswordProtected = Secret.Protect(pw.Password); _deck.SaveSoon(); };
        Add(b, F.Field("Mot de passe", pw, "Stocké sous forme chiffrée uniquement pour votre compte Windows (DPAPI) et jamais inclus dans les exports."));
        Add(b, F.Buttons(F.Button("Connecter / tester", async () =>
        {
            Apply();
            status.Text = "Connexion en cours…";
            await Task.Delay(50);
            var ok = await _deck.Obs.ConnectAsync();
            status.Text = _deck.Obs.StatusText;
            if (ok) UiHost.Instance.Toast("Connecté à OBS", ToastKind.Success);
        }, "Btn.Accent")));
        Add(b, status);

        Add(b, F.Caps("Discord"));
        Add(b, F.Hint("Discord ne dispose pas d’API de contrôle locale sans application enregistrée dans le cloud. Les boutons Discord lancent donc Discord ou le placent au premier plan, puis envoient les raccourcis clavier configurés. " +
                      "Définissez les mêmes combinaisons comme raccourcis globaux dans Discord (Paramètres utilisateur › Raccourcis clavier) afin qu’elles fonctionnent même lorsque Discord est en arrière-plan.", 0));
        Add(b, F.Field("Raccourci pour couper le micro", new ChordBox(S.DiscordMuteKeys, v => { S.DiscordMuteKeys = v; _deck.SaveSoon(); })));
        Add(b, F.Field("Raccourci pour couper le son", new ChordBox(S.DiscordDeafenKeys, v => { S.DiscordDeafenKeys = v; _deck.SaveSoon(); })));
    }

    private void Remote(StackPanel b)
    {
        Add(b, F.Hint("Utilisez une tablette ou un téléphone connecté au même réseau comme interface de contrôle : ouvrez simplement le navigateur Web, aucune application n’est à installer. " +
                      "Désactivé par défaut. Le service est accessible uniquement sur votre réseau local et chaque requête nécessite le code PIN ; aucun relais cloud n’est utilisé.", 0));
        var urls = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        void ShowUrls()
        {
            urls.Children.Clear();
            var srv = AppShell.Remote;
            if (!S.RemoteEnabled) { urls.Children.Add(F.Hint("L’interface distante est désactivée.", 0)); return; }
            if (srv?.Running != true) { urls.Children.Add(F.Hint("⚠ " + (srv?.Error ?? "Le service n’est pas en cours d’exécution."), 0)); return; }
            urls.Children.Add(F.Caps("Ouvrez l’une de ces adresses sur la tablette", 0));
            foreach (var u in RemoteServer.LocalUrls(S.RemotePort))
            {
                var tb = new TextBox { Text = u, IsReadOnly = true, FontSize = 15, Margin = new Thickness(0, 0, 0, 6) };
                urls.Children.Add(tb);
            }
            var pin = new TextBlock { Text = "Code PIN  " + S.RemotePin, FontSize = 26, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Margin = new Thickness(0, 10, 0, 0) };
            urls.Children.Add(pin);
            urls.Children.Add(F.Hint("Le pare-feu Windows peut vous demander une fois si l’application est autorisée sur les réseaux privés : autorisez-la pour que la tablette puisse se connecter.", 6));
        }
        Add(b, F.Toggle("Activer l’interface de contrôle à distance sur tablette", S.RemoteEnabled, v =>
        {
            S.RemoteEnabled = v;
            if (v && S.RemotePin.Length != 6) S.RemotePin = RemoteServer.NewPin();
            _deck.SaveSoon();
            AppShell.ApplyRemote();
            ShowUrls();
        }));
        Add(b, F.Field("Port", F.Number(S.RemotePort, 1024, 65535, v => { S.RemotePort = v; _deck.SaveSoon(); })));
        Add(b, F.Buttons(
            F.Button("Appliquer le port / redémarrer", () => { AppShell.ApplyRemote(); ShowUrls(); }),
            F.Button("Nouveau code PIN", () => { S.RemotePin = RemoteServer.NewPin(); _deck.SaveSoon(); AppShell.ApplyRemote(); ShowUrls(); })));
        Add(b, urls);
        ShowUrls();
    }

    private void Advanced(StackPanel b)
    {
        Add(b, F.Caps("Configuration", 8));
        Add(b, F.Hint($"Enregistré dans {AppPaths.Root} (profils, paramètres, sons importés). Aucun fichier n’est écrit à côté du fichier .exe.", 0));
        Add(b, F.Buttons(
            F.Button("Exporter la configuration…", () =>
            {
                var path = Pickers.Save(this, "Configuration Virtual Stream Deck|*.vsdconfig", $"VirtualStreamDeck-{DateTime.Now:yyyyMMdd}.vsdconfig");
                if (path == null) return;
                try
                {
                    _deck.SaveNow();
                    ConfigStore.WriteExport(ConfigStore.BuildExport(_deck.Config), path);
                    UiHost.Instance.Toast("Configuration exportée (sons et icônes inclus)", ToastKind.Success);
                }
                catch (Exception ex) { DialogWindow.Info(this, "Échec de l’exportation", ex.Message); }
            }),
            F.Button("Importer une configuration…", () =>
            {
                var path = Pickers.File(this, "Configuration Virtual Stream Deck|*.vsdconfig;*.vsdprofile|Tous les fichiers|*.*");
                if (path == null) return;
                try
                {
                    var pkg = ConfigStore.ReadExport(path);
                    if (pkg.Kind == "profile")
                    {
                        var added = ConfigStore.ImportProfiles(pkg, _deck.Config);
                        _deck.ReplaceConfig(_deck.Config);
                        UiHost.Instance.Toast($"{added.Count} profil(s) importé(s)", ToastKind.Success);
                        return;
                    }
                    if (!DialogWindow.Confirm(this, "Remplacer la configuration actuelle ?", "Tous les profils, boutons, sons et paramètres seront remplacés par ceux de la configuration importée. Effectuez d’abord un export si vous souhaitez conserver une sauvegarde.", "REMPLACER", true)) return;
                    _deck.ReplaceConfig(ConfigStore.ImportConfig(pkg, _deck.Config));
                    ThemeManager.Apply(S);
                    UiHost.Instance.Toast("Configuration importée", ToastKind.Success);
                    ShowSection("Avancé");
                }
                catch (Exception ex) { DialogWindow.Info(this, "Échec de l’importation", ex.Message); }
            }),
            F.Button("Ouvrir le dossier des données", () => Shell.Open(AppPaths.Root))));
        Add(b, F.Caps("Journalisation"));
        Add(b, F.ToggleWithHint("Créer un fichier journal", S.Logging, v => { S.Logging = v; Apply(); }, $"Fichiers quotidiens dans {AppPaths.Logs}. Désactivé par défaut."));
        Add(b, F.Caps("Diagnostic"));
        var diag = new TextBox
        {
            Text = DiagnosticsReport.Build(_deck), IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5,
            Height = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top,
        };
        Add(b, diag);
        Add(b, F.Buttons(
            F.Button("Copier le diagnostic", () => { try { Clipboard.SetText(diag.Text); UiHost.Instance.Toast("Diagnostic copié", ToastKind.Success); } catch (Exception ex) { UiHost.Instance.Toast(ex.Message, ToastKind.Error); } }, "Btn.Accent"),
            F.Button("Actualiser", () => diag.Text = DiagnosticsReport.Build(_deck))));
        Add(b, F.Caps("Réinitialisation"));
        Add(b, F.Buttons(
            F.Button("Réinitialiser les paramètres…", () =>
            {
                if (!DialogWindow.Confirm(this, "Réinitialiser tous les paramètres ?", "Les paramètres retrouveront leurs valeurs par défaut. Les profils, boutons et sons seront conservés.", "RÉINITIALISER", true)) return;
                var keepObs = S.ObsPasswordProtected;
                _deck.Config.Settings = new AppSettings { ObsPasswordProtected = keepObs };
                ThemeManager.Apply(_deck.Config.Settings);
                Apply(theme: true);
                AppShell.ApplyRemote();
                ShowSection("Avancé");
            }, "Btn.Danger"),
            F.Button("Relancer l’assistant de configuration…", () => AppShell.RunWizard(this))));
    }
}
