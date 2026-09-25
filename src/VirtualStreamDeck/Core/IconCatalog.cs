namespace VirtualStreamDeck.Core;

public sealed record GlyphIcon(string Code, string Name, string Category)
{
    public string Char => char.ConvertFromUtf32(Convert.ToInt32(Code, 16));
}

/// <summary>
/// Built-in icons come from the Segoe MDL2 Assets font that ships with every Windows 10/11 install,
/// so the exe carries no icon files. --verify checks each code point really exists in the font.
/// </summary>
public static class IconCatalog
{
    public const string GlyphFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    public static readonly string[] Categories =
        { "System", "Audio", "Video", "Applications", "Media", "Network", "Gaming", "Tools", "Misc" };

    public static readonly GlyphIcon[] Glyphs =
    {
        // System
        new("E713", "Settings", "System"), new("E72E", "Lock", "System"), new("E7E8", "Power", "System"),
        new("E708", "Sleep", "System"), new("E706", "Brightness", "System"), new("E7F4", "Monitor", "System"),
        new("E7F8", "Laptop", "System"), new("E770", "System", "System"), new("E9D9", "Diagnostic", "System"),
        new("E946", "Info", "System"), new("E7BA", "Warning", "System"), new("E72C", "Refresh", "System"),
        new("E80F", "Home", "System"), new("E7C4", "Task view", "System"), new("E748", "Switch user", "System"),
        new("E77B", "Contact", "System"), new("E8D7", "Permissions", "System"), new("E7B5", "Lock screen", "System"),
        new("E8A7", "New window", "System"), new("E777", "Restore", "System"),
        // Audio
        new("E720", "Microphone", "Audio"), new("EC54", "Microphone off", "Audio"), new("E767", "Volume", "Audio"),
        new("E74F", "Mute", "Audio"), new("E992", "Volume 0", "Audio"), new("E993", "Volume 1", "Audio"),
        new("E994", "Volume 2", "Audio"), new("E995", "Volume 3", "Audio"), new("E7F5", "Speakers", "Audio"),
        new("E7F6", "Headphones", "Audio"), new("E8D6", "Audio", "Audio"), new("EC4F", "Music note", "Audio"),
        new("E90B", "Music info", "Audio"), new("E189", "Sound", "Audio"),
        // Video
        new("E722", "Camera", "Video"), new("E714", "Video", "Video"), new("E7C8", "Record", "Video"),
        new("E93E", "Streaming", "Video"), new("E7B3", "Eye", "Video"), new("E890", "View", "Video"),
        new("E91B", "Photo", "Video"), new("E8B9", "Picture", "Video"), new("E7F4", "Screen", "Video"),
        // Applications
        new("E8BD", "Message", "Applications"), new("E8F2", "Chat", "Applications"), new("E715", "Mail", "Applications"),
        new("E717", "Phone", "Applications"), new("E716", "People", "Applications"), new("E8B7", "Folder", "Applications"),
        new("E8A5", "Document", "Applications"), new("E943", "Code", "Applications"), new("E756", "Terminal", "Applications"),
        new("E8EF", "Calculator", "Applications"), new("E787", "Calendar", "Applications"), new("E719", "Store", "Applications"),
        new("E8F1", "Library", "Applications"), new("E7C3", "Page", "Applications"), new("E774", "Browser", "Applications"),
        // Media
        new("E768", "Play", "Media"), new("E769", "Pause", "Media"), new("E71A", "Stop", "Media"),
        new("E892", "Previous", "Media"), new("E893", "Next", "Media"), new("E8B1", "Shuffle", "Media"),
        new("E8EE", "Repeat", "Media"), new("E8ED", "Repeat one", "Media"), new("E7C8", "Record", "Media"),
        // Network
        new("E774", "Globe", "Network"), new("E701", "Wi-Fi", "Network"), new("E702", "Bluetooth", "Network"),
        new("E839", "Ethernet", "Network"), new("E753", "Cloud", "Network"), new("E896", "Download", "Network"),
        new("E898", "Upload", "Network"), new("E71B", "Link", "Network"), new("EC05", "Network tower", "Network"),
        new("E709", "Airplane", "Network"),
        // Gaming
        new("E7FC", "Controller", "Gaming"), new("EA86", "Puzzle", "Gaming"), new("E945", "Lightning", "Gaming"),
        new("E735", "Star", "Gaming"), new("E7C1", "Flag", "Gaming"), new("E8EC", "Tag", "Gaming"),
        new("E916", "Stopwatch", "Gaming"), new("E7C8", "Clip", "Gaming"),
        // Tools
        new("E90F", "Repair", "Tools"), new("E70F", "Edit", "Tools"), new("E721", "Search", "Tools"),
        new("E71E", "Zoom", "Tools"), new("E823", "Clock", "Tools"), new("E8CB", "Sort", "Tools"),
        new("E71C", "Filter", "Tools"), new("E8F4", "New folder", "Tools"), new("E74E", "Save", "Tools"),
        new("E8C8", "Copy", "Tools"), new("E77F", "Paste", "Tools"), new("E8C6", "Cut", "Tools"),
        new("E7A7", "Undo", "Tools"), new("E7A6", "Redo", "Tools"), new("E73E", "Check", "Tools"),
        new("E710", "Add", "Tools"), new("E738", "Remove", "Tools"), new("E74D", "Delete", "Tools"),
        new("E711", "Close", "Tools"),
        // Misc
        new("E734", "Star outline", "Misc"), new("EB51", "Heart", "Misc"), new("EB52", "Heart filled", "Misc"),
        new("E76E", "Emoji", "Misc"), new("E82F", "Light bulb", "Misc"), new("E707", "Map pin", "Misc"),
        new("E81D", "Location", "Misc"), new("E8C9", "Important", "Misc"), new("E897", "Help", "Misc"),
        new("E8FB", "Accept", "Misc"), new("E783", "Error", "Misc"), new("E724", "Attach", "Misc"),
    };

    public static readonly string[] Emojis =
    {
        "🎤", "🎙", "📷", "🎥", "🔊", "🔇", "🔉", "🔈", "🎧", "▶", "⏸", "⏹", "⏺", "⏭", "⏮", "🔴", "🟢", "🟡",
        "🎬", "💬", "💻", "🖥", "🌐", "🔒", "🔓", "⚡", "🌙", "☀", "🎵", "🎶", "🎮", "🕹", "👏", "😂", "🎉", "📢",
        "🔔", "🔕", "⚠", "✅", "❌", "⭐", "❤", "🔥", "💡", "📁", "📝", "📅", "⏰", "🔍", "⚙", "🛠", "🧰", "📊",
        "📈", "🚀", "🏆", "🎯", "👋", "👍", "👎", "🙏", "💀", "👀", "🤖", "☕", "🍕", "📞", "✉", "🖱", "⌨", "🖨",
    };

    public static GlyphIcon? Find(string code) => Glyphs.FirstOrDefault(g => g.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
}
