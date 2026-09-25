# Architecture

One project, one exe, split into layers that mirror the components in the original brief.

```
src/VirtualStreamDeck
├─ Core/            pure model + logic, no UI, unit tested
│  ├─ Models.cs         AppConfig, Profile, DeckPage, DeckButton, ActionSpec, MacroStep, SoundItem…
│  ├─ ConfigStore.cs    SettingsManager: atomic JSON save, .bak, corrupt-file recovery, export/import
│  ├─ GridLayout.cs     occupancy, move/swap/transfer, Repair() (never loses a button)
│  ├─ KeyChord.cs       "CTRL + SHIFT + M" parsing / canonical form / VK table
│  ├─ Defaults.cs       profile templates, built-in sound list
│  └─ IconCatalog.cs    Segoe MDL2 / Fluent glyph catalogue + emoji
├─ Interop/Native.cs    every P/Invoke in one place + DPAPI wrapper
├─ Services/        Windows integration, no WPF controls
│  ├─ Deck.cs           ProfileManager + ButtonManager: owns config & services, press pipeline, hotkeys, auto profile
│  ├─ ActionEngine.cs   ActionEngine + MacroEngine; every failure becomes an ActionResult
│  ├─ LiveState.cs      what a key displays, derived from real system state only
│  ├─ Input.cs          SendInput builder (tested with a recording sink), window focusing
│  ├─ SystemAudio.cs    Core Audio endpoints, notifications, IPolicyConfig default device
│  ├─ MicMeter.cs       capture-stream metering (dBFS peak/RMS/hold, clip) + monitoring output
│  ├─ SoundboardEngine.cs  WASAPI mixer, cached PCM voices, fades/loops, idle device release
│  ├─ SoundSynth.cs     the eight built-in sounds, synthesised
│  ├─ CameraService.cs  consent-store watcher (in-use / blocked), PnP disable/enable (elevated)
│  ├─ SystemControl.cs  brightness (WMI + DDC/CI), radios, media session, power, CPU/RAM/net, startup
│  ├─ Hotkeys.cs        message window, HotkeyManager, ForegroundWatcher, TrayIcon
│  ├─ Displays.cs       DisplayManager
│  ├─ ObsClient.cs      obs-websocket v5
│  ├─ RemoteServer.cs   PIN-protected LAN HTTP for the tablet page (Assets/remote.html)
│  └─ DiagnosticsReport.cs
└─ UI/              WPF, code-built views over the services
   ├─ Theme/Theme.xaml, ThemeManager.cs
   ├─ Controls/  DeckButtonView, DeckGridPanel, DeckSurface, ButtonEditor (+ActionEditor, MacroEditor),
   │             Broadcast (LevelMeter, Fader, MicModuleView, BroadcastPanel), SoundLibraryPanel, CommandPalette, Form
   ├─ Windows/   MainWindow, ControlSurfaceWindow, SettingsWindow, WizardWindow, IconPickerWindow, Chrome/Dialog
   ├─ UiHost.cs  IUiHost: confirmations, missing-path dialog, toasts
   ├─ RemoteBridge.cs, SelfTest.cs
```

## Rules the code follows

- **No fake state.** Keys show live values only from a real source (endpoint volume, capture samples,
  consent store, radio API, media session, OBS events). If there is no API, the action is labelled as
  what it really does (opens settings / sends your keybind).
- **Event-driven, not polled.** Audio endpoint callbacks, IMMNotificationClient, RegNotifyChangeKeyValue,
  SetWinEventHook, Radio.StateChanged, GSMTC events, OBS events. The only timers are the 2 s status bar
  (only while a window is visible), the meter redraw (only while a meter is visible) and sound progress
  (only while a sound plays). Idle in the tray: 0 % CPU.
- **Nothing throws into the UI.** Actions return `ActionResult`; services catch and report. A missing
  exe offers *Change path*, a deleted sound says so, an unplugged monitor/device falls back with a notice,
  a corrupt config is set aside and the backup is used.
- **Focus is preserved.** The control surface is `WS_EX_NOACTIVATE`; from the main window, focus is
  handed back to the last external window before keys are injected; hotkey-triggered actions wait until
  the physical modifiers are released.
- **Least privilege.** `asInvoker`. Only "run as admin" launches and camera device disable ask for UAC.

## Data

`%APPDATA%\VirtualStreamDeck\config.json` (+ `config.bak.json`), `Sounds\` for imported audio, `Logs\`
only when logging is on. Images are stored inline (base64 PNG ≤ 256 px) so an export is self-contained.
The OBS password is DPAPI-protected for the current user and never exported.
