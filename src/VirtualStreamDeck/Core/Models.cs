using System.Text.Json.Serialization;

namespace VirtualStreamDeck.Core;

// The persisted model is deliberately plain: POCOs with champs de chaîne "commande" plutôt que des hiérarchies d’énumérations complexes, afin qu’une configuration créée par une version
// plus récente (ou modifiée manuellement) puisse toujours être chargée et qu’une valeur inconnue
// soit convertie en "action non prise en charge" au lieu de provoquer un plantage.

public static class ActionTypes
{
    public const string None = "none";
    public const string LaunchApp = "launch";
    public const string OpenUrl = "url";
    public const string Hotkey = "hotkey";
    public const string Macro = "macro";
    public const string TypeText = "text";
    public const string Power = "power";
    public const string Media = "media";
    public const string System = "system";
    public const string Sound = "sound";
    public const string Deck = "deck";
    public const string Obs = "obs";
    public const string Discord = "discord";
    public const string RunCommand = "command";
    public const string OpenPath = "open";

    public static readonly (string Id, string Label)[] All =
    {
        (None, "Aucune action"),
        (LaunchApp, "Lancer une application"),
        (OpenUrl, "Ouvrir une URL / un site web"),
        (OpenPath, "Ouvrir un fichier ou un dossier"),
        (Hotkey, "Raccourci clavier"),
        (Macro, "Macro (séquence)"),
        (TypeText, "Saisir du texte"),
        (Sound, "Lire un son"),
        (Media, "Contrôle multimédia"),
        (System, "Contrôle du système"),
        (Power, "Alimentation"),
        (Deck, "Navigation dans le panneau"),
        (Obs, "OBS Studio"),
        (Discord, "Discord"),
        (RunCommand, "Exécuter une commande / un script (défini par l’utilisateur)"),
    };

    public static string Label(string? id) =>
        All.FirstOrDefault(a => a.Id == id).Label ?? "Action inconnue";
}

public static class PowerCommands
{
    public const string Lock = "lock";
    public const string Sleep = "sleep";
    public const string Hibernate = "hibernate";
    public const string Restart = "restart";
    public const string Shutdown = "shutdown";
    public const string SignOut = "signout";

    public static readonly (string Id, string Label)[] All =
    {
        (Lock, "Verrouiller le PC"), (Sleep, "Mettre en veille"), (Hibernate, "Mettre en veille prolongée"),
        (Restart, "Redémarrer"), (Shutdown, "Éteindre"), (SignOut, "Se déconnecter"),
    };

    /// <summary>Commands that end the session or power state and always need confirmation.</summary>
    public static bool IsDestructive(string? cmd) => cmd is Sleep or Hibernate or Restart or Shutdown or SignOut;
}

public static class MediaCmds
{
    public const string PlayPause = "playpause";
    public const string Next = "next";
    public const string Previous = "previous";
    public const string Stop = "stop";
    public const string VolumeUp = "volup";
    public const string VolumeDown = "voldown";
    public const string Mute = "mute";

    public static readonly (string Id, string Label)[] All =
    {
        (PlayPause, "Lire / mettre en pause"), (Next, "Piste suivante"), (Previous, "Piste précédente"),
        (Stop, "Arrêter"), (VolumeUp, "Augmenter le volume"), (VolumeDown, "Diminuer le volume"), (Mute, "Couper / rétablir le son"),
    };
}

public static class SysCommands
{
    public const string MicToggle = "mic.toggle";
    public const string MicMute = "mic.mute";
    public const string MicUnmute = "mic.unmute";
    public const string MicVolumeUp = "mic.volup";
    public const string MicVolumeDown = "mic.voldown";
    public const string MicVolumeSet = "mic.volset";
    public const string MicMonitorToggle = "mic.monitor";
    public const string MicCycle = "mic.cycle";
    public const string MasterMuteToggle = "vol.mute";
    public const string MasterVolumeSet = "vol.set";
    public const string MasterVolumeUp = "vol.up";
    public const string MasterVolumeDown = "vol.down";
    public const string OutputCycle = "out.cycle";
    public const string OutputSet = "out.set";
    public const string InputSet = "in.set";
    public const string CameraStatus = "cam.status";
    public const string CameraDeviceToggle = "cam.device";
    public const string BrightnessUp = "bright.up";
    public const string BrightnessDown = "bright.down";
    public const string BrightnessSet = "bright.set";
    public const string WifiToggle = "wifi.toggle";
    public const string BluetoothToggle = "bt.toggle";
    public const string NightLight = "nightlight";
    public const string WindowsSettings = "settings";
    public const string TaskManager = "taskmgr";

    public static readonly (string Id, string Label)[] All =
    {
        (MicToggle, "Microphone : activer/désactiver le son"),
        (MicMute, "Microphone : couper le son"),
        (MicUnmute, "Microphone : rétablir le son"),
        (MicVolumeUp, "Microphone : augmenter le volume"),
        (MicVolumeDown, "Microphone : diminuer le volume"),
        (MicVolumeSet, "Microphone : régler le volume"),
        (MicMonitorToggle, "Microphone : retour audio (s’entendre)"),
        (MicCycle, "Microphone : passer au périphérique suivant"),
        (MasterMuteToggle, "Haut-parleurs : activer/désactiver le son"),
        (MasterVolumeUp, "Haut-parleurs : augmenter le volume"),
        (MasterVolumeDown, "Haut-parleurs : diminuer le volume"),
        (MasterVolumeSet, "Haut-parleurs : régler le volume"),
        (OutputCycle, "Sortie par défaut : parcourir les périphériques"),
        (OutputSet, "Sortie par défaut : choisir le périphérique"),
        (InputSet, "Entrée par défaut : choisir le périphérique"),
        (CameraStatus, "Caméra : état et paramètres de confidentialité"),
        (CameraDeviceToggle, "Caméra : désactiver / activer le périphérique (administrateur)"),
        (BrightnessUp, "Augmenter la luminosité"),
        (BrightnessDown, "Diminuer la luminosité"),
        (BrightnessSet, "Luminosité : régler le niveau"),
        (WifiToggle, "Wi-Fi : activer/désactiver la connexion"),
        (BluetoothToggle, "Bluetooth : activer/désactiver la connexion"),
        (NightLight, "Éclairage nocturne (ouvre les paramètres Windows)"),
        (WindowsSettings, "Ouvrir les paramètres Windows"),
        (TaskManager, "Ouvrir le Gestionnaire des tâches"),
    };
}

public static class DeckCommands
{
    public const string SwitchProfile = "profile";
    public const string NextProfile = "profile.next";
    public const string NextPage = "page.next";
    public const string PreviousPage = "page.prev";
    public const string GoToPage = "page.go";
    public const string StopAllSounds = "sounds.stop";
    public const string ToggleSurface = "surface";
    public const string ToggleAutoProfile = "autoprofile";
    public const string OpenMainWindow = "main";
    public const string Search = "search";

    public static readonly (string Id, string Label)[] All =
    {
        (SwitchProfile, "Passer au profil"),
        (NextProfile, "Profil suivant"),
        (NextPage, "Page suivante"),
        (PreviousPage, "Page précédente"),
        (GoToPage, "Aller à la page"),
        (StopAllSounds, "Arrêter tous les sons"),
        (ToggleSurface, "Afficher/masquer la surface de contrôle"),
        (ToggleAutoProfile, "Activer/désactiver le changement automatique de profil"),
        (OpenMainWindow, "Afficher la fenêtre principale"),
    };
}

public static class ObsCommands
{
    public const string StartStream = "stream.start";
    public const string StopStream = "stream.stop";
    public const string ToggleStream = "stream.toggle";
    public const string StartRecord = "record.start";
    public const string StopRecord = "record.stop";
    public const string ToggleRecord = "record.toggle";
    public const string PauseRecord = "record.pause";
    public const string Scene = "scene";
    public const string ToggleSource = "source.toggle";
    public const string ToggleMute = "input.mute";

    public static readonly (string Id, string Label)[] All =
    {
        (StartStream, "Démarrer le streaming"), (StopStream, "Arrêter le streaming"), (ToggleStream, "Activer/désactiver le streaming"),
        (StartRecord, "Démarrer l’enregistrement"), (StopRecord, "Arrêter l’enregistrement"), (ToggleRecord, "Activer/désactiver l’enregistrement"),
        (PauseRecord, "Mettre en pause / reprendre l’enregistrement"), (Scene, "Changer de scène"),
        (ToggleSource, "Afficher/masquer la source"), (ToggleMute, "Activer/désactiver le son de la source audio"),
    };
}

public static class DiscordCommands
{
    public const string Launch = "launch";
    public const string Focus = "focus";
    public const string MuteKeybind = "mute";
    public const string DeafenKeybind = "deafen";

    public static readonly (string Id, string Label)[] All =
    {
        (Launch, "Lancer Discord / activer sa fenêtre"),
        (Focus, "Activer la fenêtre Discord"),
        (MuteKeybind, "Couper le micro (utilise le raccourci Discord)"),
        (DeafenKeybind, "Désactiver le son entrant (utilise le raccourci Discord)"),
    };
}

public static class MacroStepTypes
{
    public const string Key = "key";      // single key tap
    public const string Combo = "combo";  // chord, e.g. CTRL + SHIFT + M
    public const string KeyDown = "keydown";
    public const string KeyUp = "keyup";
    public const string Text = "text";
    public const string Delay = "delay";
    public const string Click = "click";
    public const string Move = "move";
    public const string Launch = "launch";

    public static readonly (string Id, string Label)[] All =
    {
        (Combo, "Combinaison de touches"), (Key, "Touche"), (Text, "Saisir du texte"), (Delay, "Attendre"),
        (Click, "Clic de souris"), (Move, "Déplacement de la souris"), (Launch, "Lancer une application"),
        (KeyDown, "Touche enfoncée (maintenir)"), (KeyUp, "Touche relâchée (relâcher)"),
    };
}

public sealed class MacroStep
{
    public string Type { get; set; } = MacroStepTypes.Combo;
    /// <summary>Key chord / key name / text / exe path, depending on <see cref="Type"/>.</summary>
    public string Value { get; set; } = "";
    /// <summary>Delay in ms, or mouse button ("left"/"right"/"middle") for clicks.</summary>
    public int Number { get; set; }
    public string Button { get; set; } = "left";
    public int X { get; set; }
    public int Y { get; set; }
    /// <summary>Click at X/Y instead of the current cursor position.</summary>
    public bool AtPosition { get; set; }

    public string Describe() => Type switch
    {
        MacroStepTypes.Combo => Value.Length == 0 ? "Combinaison de touches (non définie)" : Value,
        MacroStepTypes.Key => Value.Length == 0 ? "Touche (non définie)" : Value,
        MacroStepTypes.KeyDown => $"MAINTENIR {Value}",
        MacroStepTypes.KeyUp => $"RELÂCHER {Value}",
        MacroStepTypes.Text => $"SAISIR \"{Value}\"",
        MacroStepTypes.Delay => $"ATTENDRE {Number} ms",
        MacroStepTypes.Click => AtPosition ? $"CLIC {Button.ToUpperInvariant()} aux coordonnées {X},{Y}" : $"CLIC {Button.ToUpperInvariant()}",
        MacroStepTypes.Move => $"DÉPLACER LA SOURIS vers {X},{Y}",
        MacroStepTypes.Launch => $"LANCER {Path.GetFileName(Value)}",
        _ => Type,
    };
}

public sealed class ActionSpec
{
    public string Type { get; set; } = ActionTypes.None;
    /// <summary>Sub-command for power/media/system/deck/obs/discord actions.</summary>
    public string Command { get; set; } = "";
    /// <summary>Executable / file / folder / URL.</summary>
    public string Target { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    /// <summary>Key chord for Hotkey actions, e.g. "CTRL + SHIFT + M".</summary>
    public string Keys { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Numeric parameter (volume %, step, page number...).</summary>
    public int Value { get; set; }
    /// <summary>Generic string parameter: profile id, device id, scene name, sound id...</summary>
    public string Param { get; set; } = "";
    public string Param2 { get; set; } = "";
    public List<MacroStep> Steps { get; set; } = new();

    // Sound playback options
    public int Volume { get; set; } = 100;
    public bool Loop { get; set; }
    public int FadeInMs { get; set; }
    public int FadeOutMs { get; set; }
    /// <summary>"overlap" | "restart" | "toggle" | "" (use global default).</summary>
    public string Retrigger { get; set; } = "";

    // Launch options
    public bool FocusIfRunning { get; set; } = true;
    public bool RunAsAdmin { get; set; }

    // RunCommand options: "powershell" | "cmd"
    public string Shell { get; set; } = "powershell";
    public bool ShowWindow { get; set; }

    public static ActionSpec Of(string type, string command = "") => new() { Type = type, Command = command };
}

public static class IconKinds
{
    public const string Glyph = "glyph";
    public const string Emoji = "emoji";
    public const string Text = "text";
    public const string Image = "image";
}

public sealed class IconSpec
{
    public string Kind { get; set; } = IconKinds.Glyph;
    /// <summary>Glyph code point (hex, e.g. "E720"), emoji, or short text.</summary>
    public string Value { get; set; } = "";
    /// <summary>Base64 PNG for image icons, downscaled on import so configs stay small.</summary>
    public string? ImageData { get; set; }

    public static IconSpec Glyph(string code) => new() { Kind = IconKinds.Glyph, Value = code };
    public static IconSpec Emoji(string e) => new() { Kind = IconKinds.Emoji, Value = e };
    public static IconSpec TextIcon(string t) => new() { Kind = IconKinds.Text, Value = t };
    public bool IsEmpty => Kind != IconKinds.Image ? string.IsNullOrEmpty(Value) : string.IsNullOrEmpty(ImageData);
}

public static class ButtonModes
{
    public const string Standard = "standard";
    public const string Momentary = "momentary";
    public const string Toggle = "toggle";
    public const string MultiState = "multi";

    public static readonly (string Id, string Label)[] All =
    {
        (Standard, "Standard (appuyer pour exécuter)"),
        (Momentary, "Momentané (actif tant que la touche est maintenue)"),
        (Toggle, "Bascule (deux états)"),
        (MultiState, "À états multiples (défilement)"),
    };
}

public sealed class ButtonState
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public IconSpec? Icon { get; set; }
    public string? Accent { get; set; }
    public ActionSpec Action { get; set; } = new();
}
public static class ButtonFeedbackModes
{
    public const string Inherit = "inherit";
    public const string None = "none";
    public const string Press = "press";
    public const string Flash = "flash";
    public const string Shake = "shake";

    public static readonly (string Id, string Label)[] All =
    {
        (Inherit, "Utiliser le réglage global"),
        (None, "Aucun retour visuel"),
        (Press, "Enfoncement uniquement"),
        (Flash, "Flash de couleur"),
        (Shake, "Flash et vibration"),
    };
}
public sealed class DeckButton
{
    public string Id { get; set; } = NewId();
    public int Row { get; set; }
    public int Col { get; set; }
    public int RowSpan { get; set; } = 1;
    public int ColSpan { get; set; } = 1;

    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public IconSpec Icon { get; set; } = new();
    /// <summary>Hex colours (#RRGGBB); null signifie "utiliser le thème".</summary>
    public string? Background { get; set; }
    public string? Accent { get; set; }
    public string? TextColor { get; set; }

    public string Mode { get; set; } = ButtonModes.Standard;
    public ActionSpec Action { get; set; } = new();
    /// <summary>Extra states for Toggle/MultiState. State 0 is the button's own title/icon/action.</summary>
    public List<ButtonState> States { get; set; } = new();
    public int CurrentState { get; set; }

    /// <summary>Global hotkey that presses this button from anywhere.</summary>
    public string Hotkey { get; set; } = "";
    /// <summary>Feedback sound played on press (sound library id).</summary>
    public string SoundId { get; set; } = "";
    public bool Confirm { get; set; }
    public string ConfirmText { get; set; } = "";
    /// <summary>When false the live-state text (MUTED / ON...) is not drawn in the subtitle.</summary>
    public bool ShowLiveState { get; set; } = true;

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

public sealed class DeckPage
{
    public string Id { get; set; } = DeckButton.NewId();
    public string Name { get; set; } = "";
    public List<DeckButton> Buttons { get; set; } = new();
}

public sealed class Profile
{
    public string Id { get; set; } = DeckButton.NewId();
    public string Name { get; set; } = "Profile";
    public string Glyph { get; set; } = "E80F";
    public int Columns { get; set; } = 5;
    public int Rows { get; set; } = 3;
    public List<DeckPage> Pages { get; set; } = new() { new DeckPage() };
    /// <summary>Global hotkey that switches to this profile.</summary>
    public string Hotkey { get; set; } = "";
}

public sealed class SoundItem
{
    public string Id { get; set; } = DeckButton.NewId();
    public string Name { get; set; } = "";
    /// <summary>File name inside the Sounds data folder, or "builtin:&lt;key&gt;" for synthesised sounds.</summary>
    public string File { get; set; } = "";
    public bool Favorite { get; set; }
    public int Volume { get; set; } = 100;

    [JsonIgnore] public bool IsBuiltIn => File.StartsWith("builtin:", StringComparison.Ordinal);
}

public sealed class AutoProfileRule
{
    public string Process { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public static class GlobalCommands
{
    public const string ToggleMic = "mic";
    public const string ToggleSurface = "surface";
    public const string ShowMain = "main";
    public const string StopSounds = "stopsounds";
    public const string NextProfile = "nextprofile";
    public const string ToggleAutoProfile = "autoprofile";
    public const string ProfilePrefix = "profile:";
    public const string ButtonPrefix = "button:";

    public static readonly (string Id, string Label)[] Fixed =
    {
        (ToggleMic, "Activer/désactiver le microphone"),
        (ToggleSurface, "Afficher/masquer la surface de contrôle"),
        (ShowMain, "Afficher la fenêtre principale"),
        (StopSounds, "Arrêter tous les sons"),
        (NextProfile, "Profil suivant"),
        (ToggleAutoProfile, "Activer/désactiver le profil automatique"),
    };
}

public sealed class HotkeyBinding
{
    public string Chord { get; set; } = "";
    public string Command { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

public sealed class AppSettings
{
    // General
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool RememberWindowPosition { get; set; } = true;
    public WindowPlacement? MainWindow { get; set; }
    public string Theme { get; set; } = "charcoal";
    public string AccentColor { get; set; } = "#3B82F6";
    public string Language { get; set; } = "en";
    public bool ShowToasts { get; set; } = true;

    // Appearance
    /// <summary>"fill" | "large" | "medium" | "small": caps the size of one grid cell.</summary>
    public string ButtonSize { get; set; } = "fill";
    public bool SquareButtons { get; set; } = true;
    public double CornerRadius { get; set; } = 14;
    /// <summary>0 = off, 1 = subtle, 2 = full.</summary>
    public int AnimationLevel { get; set; } = 1;
    public double FontScale { get; set; } = 1.0;
    public bool CompactMode { get; set; }
    public int DefaultColumns { get; set; } = 5;
    public int DefaultRows { get; set; } = 3;
    public bool TriggerOnRelease { get; set; }

    // Control surface
    public bool SurfaceFullscreen { get; set; } = true;
    public string SurfaceDisplay { get; set; } = "";
    public bool RememberDisplay { get; set; } = true;
    public bool SurfaceAutoHideCursor { get; set; }
    public int SurfaceCursorHideSeconds { get; set; } = 3;
    public bool SurfacePreventSleep { get; set; } = true;
    public bool TouchMode { get; set; }
    public bool SurfaceNoActivate { get; set; } = true;
    public bool SurfaceTopmost { get; set; }
    public bool SurfaceShowStatus { get; set; } = true;
    public bool SurfaceOpenOnStart { get; set; }

    // Audio
    public string SoundboardDevice { get; set; } = "";
    public int SoundboardVolume { get; set; } = 80;
    public int DefaultFadeMs { get; set; } = 250;
    /// <summary>"overlap" | "restart" | "toggle"</summary>
    public string SoundOverlap { get; set; } = "overlap";
    /// <summary>Capture endpoint the mic button controls; empty = Windows default recording device.</summary>
    public string MicrophoneDevice { get; set; } = "";
    public int VolumeStep { get; set; } = 5;
    /// <summary>Output used to monitor the microphone ("" = Windows default output).</summary>
    public string MicMonitorDevice { get; set; } = "";
    public int MicMonitorVolume { get; set; } = 80;
    /// <summary>Re-apply the last volume set in this app when a microphone (re)connects.</summary>
    public bool MicRestoreVolume { get; set; } = true;
    /// <summary>Last microphone volume set in this app, per capture endpoint id.</summary>
    public Dictionary<string, int> MicVolumes { get; set; } = new();
    /// <summary>Draw live input meters on microphone keys (keeps the microphone stream open).</summary>
    public bool MeterOnButtons { get; set; }
    public bool SurfaceShowBroadcast { get; set; }

    // Profiles
    public bool AutoProfile { get; set; }
    /// <summary>Profile to return to when no rule matches ("" = stay on the current one).</summary>
    public string AutoProfileFallback { get; set; } = "";

    // Integrations
    public bool ObsEnabled { get; set; }
    public string ObsHost { get; set; } = "127.0.0.1";
    public int ObsPort { get; set; } = 4455;
    /// <summary>DPAPI-protected (current user) OBS password. Never exported.</summary>
    public string ObsPasswordProtected { get; set; } = "";
    public string DiscordMuteKeys { get; set; } = "CTRL + SHIFT + M";
    public string DiscordDeafenKeys { get; set; } = "CTRL + SHIFT + D";

    public bool RemoteEnabled { get; set; }
    public int RemotePort { get; set; } = 8765;
    public string RemotePin { get; set; } = "";

    // Advanced
    public bool Logging { get; set; }
}

public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 1;
    public bool FirstRunCompleted { get; set; }
    public AppSettings Settings { get; set; } = new();
    public List<Profile> Profiles { get; set; } = new();
    public string ActiveProfileId { get; set; } = "";
    public int ActivePage { get; set; }
    public List<SoundItem> Sounds { get; set; } = new();
    public List<AutoProfileRule> AutoProfileRules { get; set; } = new();
    public List<HotkeyBinding> Hotkeys { get; set; } = new();
}
