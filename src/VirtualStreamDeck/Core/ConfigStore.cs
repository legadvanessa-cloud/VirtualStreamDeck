using System.Text.Json;

namespace VirtualStreamDeck.Core;

public sealed record LoadResult(AppConfig Config, bool WasCreated, string? Warning);

/// <summary>
/// SettingsManager: owns config.json in %APPDATA%. Writes are atomic (temp file + replace, previous
/// version kept as config.bak.json) and a corrupt file never blocks startup - it is set aside,
/// the backup is tried, and only then do defaults take over.
/// </summary>
public static class ConfigStore
{
    private static readonly object WriteLock = new();

    public static LoadResult Load()
    {
        AppPaths.Ensure();
        if (!File.Exists(AppPaths.Config) && !File.Exists(AppPaths.ConfigBackup))
            return new LoadResult(Defaults.CreateConfig(), true, null);

        string? warning = null;
        foreach (var path in new[] { AppPaths.Config, AppPaths.ConfigBackup })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var cfg = Json.Deserialize<AppConfig>(File.ReadAllText(path)) ?? throw new InvalidDataException("empty document");
                var repaired = Sanitize(cfg);
                if (path == AppPaths.ConfigBackup)
                    warning = "The configuration file was damaged; the previous saved version was restored.";
                else if (repaired > 0)
                    warning = $"{repaired} button(s) overlapped or were outside their grid and were moved to a free slot.";
                return new LoadResult(cfg, false, warning);
            }
            catch (Exception ex)
            {
                Log.Error($"Could not read {Path.GetFileName(path)}", ex);
                SetAside(path);
            }
        }
        return new LoadResult(Defaults.CreateConfig(), true,
            "The configuration could not be read and was reset. The damaged file was kept next to it for recovery.");
    }

    private static void SetAside(string path)
    {
        try { File.Move(path, Path.ChangeExtension(path, $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json"), true); }
        catch (Exception ex) { Log.Error("Could not set aside damaged config", ex); }
    }

    public static void Save(AppConfig cfg)
    {
        var json = Json.Serialize(cfg);
        lock (WriteLock)
        {
            AppPaths.Ensure();
            var tmp = AppPaths.Config + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(AppPaths.Config))
                File.Replace(tmp, AppPaths.Config, AppPaths.ConfigBackup, ignoreMetadataErrors: true);
            else
                File.Move(tmp, AppPaths.Config);
        }
    }

    /// <summary>Makes a config structurally safe to use. Returns how many buttons had to be re-homed.</summary>
    public static int Sanitize(AppConfig cfg)
    {
        cfg.Settings ??= new AppSettings();
        cfg.Profiles ??= new List<Profile>();
        cfg.Sounds ??= new List<SoundItem>();
        cfg.AutoProfileRules ??= new List<AutoProfileRule>();
        cfg.Hotkeys ??= new List<HotkeyBinding>();
        cfg.Profiles.RemoveAll(p => p == null);
        cfg.Sounds.RemoveAll(s => s == null || string.IsNullOrEmpty(s.File));
        cfg.Hotkeys.RemoveAll(h => h == null);
        cfg.AutoProfileRules.RemoveAll(r => r == null);

        // Built-in sounds are part of the app, not the user's data - make sure they are always listed.
        foreach (var b in Defaults.CreateBuiltInSounds())
            if (cfg.Sounds.All(s => s.Id != b.Id)) cfg.Sounds.Add(b);

        if (cfg.Profiles.Count == 0) cfg.Profiles.Add(Defaults.CreateProfile("general", 5, 3));
        var ids = new HashSet<string>();
        var moved = 0;
        var buttonIds = new HashSet<string>();
        foreach (var p in cfg.Profiles)
        {
            if (string.IsNullOrEmpty(p.Id) || !ids.Add(p.Id)) { p.Id = DeckButton.NewId(); ids.Add(p.Id); }
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "Profile";
            moved += GridLayout.Repair(p);
            foreach (var b in p.Pages.SelectMany(pg => pg.Buttons))
                if (!buttonIds.Add(b.Id)) { b.Id = DeckButton.NewId(); buttonIds.Add(b.Id); }
        }
        if (cfg.Profiles.All(p => p.Id != cfg.ActiveProfileId)) cfg.ActiveProfileId = cfg.Profiles[0].Id;
        var s = cfg.Settings;
        s.SoundboardVolume = Math.Clamp(s.SoundboardVolume, 0, 100);
        s.DefaultFadeMs = Math.Clamp(s.DefaultFadeMs, 0, 10000);
        s.CornerRadius = Math.Clamp(s.CornerRadius, 0, 40);
        s.FontScale = Math.Clamp(s.FontScale, 0.7, 1.6);
        s.AnimationLevel = Math.Clamp(s.AnimationLevel, 0, 2);
        s.VolumeStep = Math.Clamp(s.VolumeStep, 1, 25);
        s.RemotePort = Math.Clamp(s.RemotePort, 1024, 65535);
        s.ObsPort = Math.Clamp(s.ObsPort, 1, 65535);
        return moved;
    }

    // ---- export / import --------------------------------------------------------------------

    public const string ExportFormat = "VirtualStreamDeck.Export";

    public sealed class ExportedSound
    {
        public SoundItem Item { get; set; } = new();
        public string Data { get; set; } = "";
    }

    public sealed class ExportPackage
    {
        public string Format { get; set; } = ExportFormat;
        public int Version { get; set; } = 1;
        public string Kind { get; set; } = "config"; // "config" | "profile"
        public string App { get; set; } = AppInfo.Version;
        public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
        public AppConfig? Config { get; set; }
        public List<Profile>? Profiles { get; set; }
        public List<ExportedSound> Sounds { get; set; } = new();
    }

    /// <summary>
    /// Complete, machine-independent package: settings, profiles, icons (already inline) and the
    /// audio of every imported sound. Secrets (OBS password, remote PIN) are stripped.
    /// </summary>
    public static ExportPackage BuildExport(AppConfig cfg)
    {
        var copy = Json.Clone(cfg);
        StripSecrets(copy.Settings);
        copy.Settings.MainWindow = null;
        var pkg = new ExportPackage { Kind = "config", Config = copy };
        AddSounds(pkg, copy.Sounds);
        return pkg;
    }

    public static ExportPackage BuildProfileExport(AppConfig cfg, Profile profile)
    {
        var p = Json.Clone(profile);
        var pkg = new ExportPackage { Kind = "profile", Profiles = new() { p } };
        var used = ReferencedSounds(p);
        AddSounds(pkg, cfg.Sounds.Where(s => used.Contains(s.Id)));
        return pkg;
    }

    private static void StripSecrets(AppSettings s)
    {
        s.ObsPasswordProtected = "";
        s.RemotePin = "";
    }

    private static HashSet<string> ReferencedSounds(Profile p)
    {
        var set = new HashSet<string>();
        foreach (var b in p.Pages.SelectMany(pg => pg.Buttons))
        {
            if (!string.IsNullOrEmpty(b.SoundId)) set.Add(b.SoundId);
            foreach (var a in new[] { b.Action }.Concat(b.States.Select(s => s.Action)))
                if (a.Type == ActionTypes.Sound && !string.IsNullOrEmpty(a.Param)) set.Add(a.Param);
        }
        return set;
    }

    private static void AddSounds(ExportPackage pkg, IEnumerable<SoundItem> sounds)
    {
        foreach (var s in sounds.Where(s => !s.IsBuiltIn))
        {
            var path = Path.Combine(AppPaths.Sounds, s.File);
            if (!File.Exists(path)) { Log.Warn($"Export: sound file missing, skipped: {s.File}"); continue; }
            pkg.Sounds.Add(new ExportedSound { Item = Json.Clone(s), Data = Convert.ToBase64String(File.ReadAllBytes(path)) });
        }
    }

    public static void WriteExport(ExportPackage pkg, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(pkg, new JsonSerializerOptions(Json.Options) { WriteIndented = false }));

    public static ExportPackage ReadExport(string path)
    {
        ExportPackage? pkg;
        try { pkg = Json.Deserialize<ExportPackage>(File.ReadAllText(path)); }
        catch (JsonException ex) { throw new InvalidDataException("The file is not a valid Virtual Stream Deck export (" + ex.Message + ")."); }
        if (pkg == null || pkg.Format != ExportFormat)
            throw new InvalidDataException("The file is not a Virtual Stream Deck export.");
        if (pkg.Kind == "config" && pkg.Config == null) throw new InvalidDataException("The export contains no configuration.");
        if (pkg.Kind == "profile" && (pkg.Profiles == null || pkg.Profiles.Count == 0)) throw new InvalidDataException("The export contains no profile.");
        return pkg;
    }

    /// <summary>Writes the package's audio into the Sounds folder, returning old-id → imported item.</summary>
    private static Dictionary<string, SoundItem> ImportSounds(ExportPackage pkg, AppConfig target)
    {
        var map = new Dictionary<string, SoundItem>();
        AppPaths.Ensure();
        foreach (var es in pkg.Sounds)
        {
            byte[] data;
            try { data = Convert.FromBase64String(es.Data); } catch { continue; }
            var existing = target.Sounds.FirstOrDefault(s => s.Id == es.Item.Id);
            var item = existing ?? Json.Clone(es.Item);
            var ext = Path.GetExtension(es.Item.File);
            if (!SoundFiles.IsSupported(ext)) continue;
            var file = existing?.File is { Length: > 0 } f && !existing.IsBuiltIn ? f : SoundFiles.UniqueName(es.Item.Name, ext);
            File.WriteAllBytes(Path.Combine(AppPaths.Sounds, file), data);
            item.File = file;
            if (existing == null) target.Sounds.Add(item);
            map[es.Item.Id] = item;
        }
        return map;
    }

    /// <summary>Replaces the whole configuration with an imported one (keeps this machine's secrets and window placement).</summary>
    public static AppConfig ImportConfig(ExportPackage pkg, AppConfig current)
    {
        var incoming = Json.Clone(pkg.Config!);
        incoming.Settings.ObsPasswordProtected = current.Settings.ObsPasswordProtected;
        incoming.Settings.RemotePin = current.Settings.RemotePin;
        incoming.Settings.MainWindow = current.Settings.MainWindow;
        incoming.Sounds = incoming.Sounds.Where(s => s.IsBuiltIn).ToList();
        ImportSounds(pkg, incoming);
        incoming.FirstRunCompleted = true;
        Sanitize(incoming);
        return incoming;
    }

    /// <summary>Adds the package's profile(s) to the current config under fresh ids.</summary>
    public static List<Profile> ImportProfiles(ExportPackage pkg, AppConfig current)
    {
        ImportSounds(pkg, current);
        var added = new List<Profile>();
        foreach (var src in pkg.Profiles!)
        {
            var p = Json.Clone(src);
            p.Id = DeckButton.NewId();
            p.Hotkey = "";
            if (current.Profiles.Any(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))) p.Name += " (imported)";
            foreach (var b in p.Pages.SelectMany(pg => pg.Buttons)) { b.Id = DeckButton.NewId(); b.Hotkey = ""; }
            GridLayout.Repair(p);
            current.Profiles.Add(p);
            added.Add(p);
        }
        return added;
    }
}

public static class SoundFiles
{
    public static readonly string[] Extensions = { ".wav", ".mp3", ".ogg", ".aiff", ".aif", ".wma", ".m4a", ".aac", ".flac" };
    public static bool IsSupported(string? ext) => ext != null && Extensions.Contains(ext.ToLowerInvariant());
    public const string DialogFilter = "Audio files|*.wav;*.mp3;*.ogg;*.aiff;*.aif;*.wma;*.m4a;*.aac;*.flac|All files|*.*";

    public static string UniqueName(string name, string ext)
    {
        var safe = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        if (safe.Length == 0) safe = "sound";
        if (safe.Length > 60) safe = safe[..60];
        var file = safe + ext.ToLowerInvariant();
        var n = 2;
        while (File.Exists(Path.Combine(AppPaths.Sounds, file))) file = $"{safe} ({n++}){ext.ToLowerInvariant()}";
        return file;
    }
}
