using System.Globalization;

namespace VirtualStreamDeck.UI;

/// <summary>French labels for the built-in interface. Persisted identifiers and user data are never translated.</summary>
public static class French
{
    private static readonly Dictionary<string, string> Text = new(StringComparer.Ordinal)
    {
        ["Startup"] = "Démarrage", ["Start with Windows"] = "Démarrer avec Windows",
        ["Start minimized"] = "Démarrer réduit", ["Window"] = "Fenêtre",
        ["Minimize to the system tray"] = "Réduire dans la zone de notification",
        ["Close button keeps running in the tray"] = "Continuer à fonctionner dans la zone de notification après fermeture",
        ["Remember window position"] = "Mémoriser la position de la fenêtre", ["Notifications"] = "Notifications",
        ["Language"] = "Langue", ["English"] = "Français", ["This version ships in English only."] = "Cette version est disponible en français.",
        ["General"] = "Général", ["Appearance"] = "Apparence", ["Control Surface"] = "Surface de contrôle",
        ["Audio"] = "Audio", ["Broadcast Mic"] = "Micro de diffusion", ["Profiles"] = "Profils",
        ["Hotkeys"] = "Raccourcis clavier", ["Integrations"] = "Intégrations", ["Tablet Remote"] = "Télécommande tablette", ["Advanced"] = "Avancé",
        ["Theme"] = "Thème", ["Accent colour"] = "Couleur d’accentuation", ["Buttons"] = "Boutons",
        ["Default grid for new profiles"] = "Grille par défaut des nouveaux profils", ["Button size"] = "Taille des boutons",
        ["Fill the screen"] = "Remplir l’écran", ["Large (max 210 px)"] = "Grand (max. 210 px)",
        ["Medium (max 150 px)"] = "Moyen (max. 150 px)", ["Small (max 110 px)"] = "Petit (max. 110 px)",
        ["Square buttons"] = "Boutons carrés", ["Corner radius"] = "Arrondi des coins", ["Font size"] = "Taille du texte",
        ["Animation"] = "Animation", ["Off"] = "Désactivée", ["Subtle"] = "Discrète", ["Full"] = "Complète",
        ["Compact mode"] = "Mode compact", ["Trigger on release"] = "Déclencher au relâchement",
        ["Display"] = "Écran", ["Automatic (first secondary display)"] = "Automatique (premier écran secondaire)",
        ["Touch"] = "Écran tactile", ["Full screen (no window frame)"] = "Plein écran (sans bordure)",
        ["Always on top"] = "Toujours au premier plan", ["Soundboard output"] = "Sortie audio du soundboard",
        ["Soundboard volume"] = "Volume du soundboard", ["Volume keys"] = "Touches de volume",
        ["Controlled microphone"] = "Microphone contrôlé", ["Monitoring"] = "Retour audio",
        ["Monitor output"] = "Sortie du retour audio", ["Monitor volume"] = "Volume du retour audio",
        ["Live console"] = "Console en direct", ["Rules"] = "Règles", ["OBS Studio"] = "OBS Studio",
        ["Discord"] = "Discord", ["Host"] = "Hôte", ["Port"] = "Port", ["Password"] = "Mot de passe",
        ["Connect / test"] = "Connexion / test", ["Mute keybind"] = "Raccourci pour couper le micro",
        ["Deafen keybind"] = "Raccourci pour couper le son", ["Configuration"] = "Configuration",
        ["Logging"] = "Journalisation", ["Diagnostics"] = "Diagnostics", ["Reset"] = "Réinitialisation",
        ["Browse…"] = "Parcourir…", ["Clear"] = "Effacer", ["None"] = "Aucun", ["Choose…"] = "Choisir…",
        ["No action"] = "Aucune action", ["Launch application"] = "Lancer une application",
        ["Open URL / website"] = "Ouvrir une URL / un site web", ["Open file or folder"] = "Ouvrir un fichier ou un dossier",
        ["Keyboard shortcut"] = "Raccourci clavier", ["Macro (sequence)"] = "Macro (séquence)",
        ["Type text"] = "Saisir du texte", ["Play sound"] = "Lire un son", ["Media control"] = "Contrôle multimédia",
        ["System control"] = "Contrôle du système", ["Power"] = "Alimentation", ["Deck navigation"] = "Navigation dans le deck",
        ["Run command / script (user-defined)"] = "Exécuter une commande / un script (personnalisé)",
        ["Lock PC"] = "Verrouiller le PC", ["Sleep"] = "Mettre en veille", ["Hibernate"] = "Mettre en veille prolongée",
        ["Restart"] = "Redémarrer", ["Shut down"] = "Arrêter", ["Sign out"] = "Se déconnecter",
        ["Play / Pause"] = "Lecture / Pause", ["Next track"] = "Piste suivante", ["Previous track"] = "Piste précédente",
        ["Stop"] = "Arrêter", ["Volume up"] = "Augmenter le volume", ["Volume down"] = "Baisser le volume", ["Mute / unmute"] = "Couper / rétablir le son",
        ["Start streaming"] = "Démarrer la diffusion", ["Stop streaming"] = "Arrêter la diffusion", ["Toggle streaming"] = "Activer/désactiver la diffusion",
        ["Start recording"] = "Démarrer l’enregistrement", ["Stop recording"] = "Arrêter l’enregistrement", ["Toggle recording"] = "Activer/désactiver l’enregistrement",
        ["Pause / resume recording"] = "Mettre en pause / reprendre l’enregistrement", ["Switch scene"] = "Changer de scène",
        ["Toggle source visibility"] = "Afficher/masquer la source", ["Toggle audio source mute"] = "Couper/rétablir le son de la source audio",
        ["Toggle microphone"] = "Activer/désactiver le microphone", ["Toggle control surface"] = "Afficher/masquer la surface de contrôle",
        ["Show main window"] = "Afficher la fenêtre principale", ["Stop all sounds"] = "Arrêter tous les sons",
        ["Next profile"] = "Profil suivant", ["Toggle auto profile"] = "Activer/désactiver le changement automatique de profil",
        ["Key combination"] = "Combinaison de touches", ["Key"] = "Touche", ["Type text"] = "Saisir du texte",
        ["Wait"] = "Attendre", ["Mouse click"] = "Clic de souris", ["Mouse move"] = "Déplacer la souris",
        ["Launch app"] = "Lancer une application", ["Standard (press to run)"] = "Standard (appuyer pour exécuter)",
        ["Momentary (active while held)"] = "Momentané (actif pendant l’appui)", ["Toggle (two states)"] = "Bascule (deux états)",
        ["Multi-state (cycles)"] = "États multiples (cycle)",
        ["Minimize"] = "Réduire", ["Maximize"] = "Agrandir", ["Close"] = "Fermer", ["CANCEL"] = "ANNULER",
        ["CHANGE PATH"] = "MODIFIER LE CHEMIN", ["OK"] = "OK", ["Button"] = "Bouton", ["Deck button"] = "Bouton du deck",
        ["Add a button here"] = "Ajouter un bouton ici", ["Shortcut:"] = "Raccourci :",
        ["Apply"] = "Appliquer", ["Refresh"] = "Actualiser", ["Remove"] = "Supprimer", ["Open"] = "Ouvrir",
        ["Exit"] = "Quitter", ["Settings"] = "Paramètres", ["SURFACE"] = "SURFACE",
        ["ON AIR"] = "EN DIRECT", ["MUTE"] = "MUET", ["MONITOR"] = "RETOUR",
    };

    public static string T(string value)
    {
        if (Text.TryGetValue(value, out var translated)) return translated;
        foreach (var (english, french) in Text)
            if (value.StartsWith(english + " — ", StringComparison.Ordinal)) return french + value[english.Length..];
        return value;
    }
}
