using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace FrameTrail.App;

/// <summary>
/// Application-wide appearance (system / light / dark). Mac AppPreferences.appearancePreference:
/// the default for a fresh install is dark, one value drives every window, and "system" follows
/// the OS setting live.
///
/// The palette brushes in Application.Resources are updated in place (Color is changed on the
/// existing SolidColorBrush) so code that captured a brush object earlier (export window, dialogs)
/// also follows the theme without being rebuilt.
/// </summary>
internal static class AppTheme
{
    public const string FollowSystem = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    private static readonly string[] Keys = ["Workspace", "Surface", "Chrome", "Ink", "Secondary", "Hairline", "ControlSurface", "Selection", "Accent", "Dot"];
    private static readonly string[] DarkColors = ["#252829", "#303334", "#393D3E", "#F1F2F2", "#A6ABAD", "#484D4F", "#3B3F40", "#35584B", "#62B693", "#4B5052"];
    private static readonly string[] LightColors = ["#F4F6F6", "#FCFCFC", "#FFFFFF", "#303232", "#707579", "#E6E7E8", "#F2F2F4", "#E1F2EA", "#46A986", "#D9DDDC"];
    private static bool subscribed;

    /// <summary>Selected preference: "system", "light" or "dark".</summary>
    public static string Mode { get; private set; } = Dark;

    /// <summary>False until SetMode has run once (diagnostic launches may skip App startup).</summary>
    public static bool Initialized { get; private set; }

    /// <summary>The appearance that is actually applied right now.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>Raised on the UI thread after the palette changed because the system theme changed.</summary>
    public static event Action? SystemChanged;

    public static string Normalize(string? mode) => mode switch
    {
        FollowSystem => FollowSystem,
        Light => Light,
        Dark => Dark,
        _ => Dark
    };

    /// <summary>HKCU ...\Themes\Personalize\AppsUseLightTheme (0 = dark apps). Missing key means light.</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch { return false; }
    }

    /// <summary>Applies the mode to Application.Resources and (un)subscribes the live system listener.</summary>
    public static void SetMode(string? mode)
    {
        Mode = Normalize(mode);
        Initialized = true;
        UpdateSubscription();
        Apply(Mode == FollowSystem ? SystemPrefersDark() : Mode == Dark);
    }

    /// <summary>
    /// Reads the stored preference straight from settings.json so the theme is correct before the first
    /// window is created. New key "Appearance" wins; the legacy bool DarkTheme is migrated
    /// (true -> dark, false -> light); no file or no value means dark.
    /// </summary>
    public static string ReadStoredMode()
    {
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "settings.json");
            if (!File.Exists(file)) return Dark;
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Dark;
            if (root.TryGetProperty("Appearance", out var appearance) && appearance.ValueKind == JsonValueKind.String)
                return Normalize(appearance.GetString());
            if (root.TryGetProperty("DarkTheme", out var legacy) && legacy.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return legacy.GetBoolean() ? Dark : Light;
        }
        catch { }
        return Dark;
    }

    /// <summary>Call once at startup before windows are created.</summary>
    public static void InitializeFromSettings() => SetMode(ReadStoredMode());

    private static void Apply(bool dark)
    {
        IsDark = dark;
        var app = Application.Current;
        if (app is null) return;
        var colors = dark ? DarkColors : LightColors;
        for (var i = 0; i < Keys.Length; i++)
        {
            var color = (Color)ColorConverter.ConvertFromString(colors[i]);
            if (app.Resources[Keys[i]] is SolidColorBrush { IsFrozen: false } existing) existing.Color = color;
            else app.Resources[Keys[i]] = new SolidColorBrush(color);
        }
    }

    private static void UpdateSubscription()
    {
        if (Mode == FollowSystem && !subscribed)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            subscribed = true;
        }
        else if (Mode != FollowSystem && subscribed)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            subscribed = false;
        }
    }

    // SystemEvents raises on its own thread; marshal to the UI thread before touching brushes.
    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
        var dispatcher = Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(() =>
        {
            if (Mode != FollowSystem) return;
            var dark = SystemPrefersDark();
            if (dark == IsDark) return;
            Apply(dark);
            SystemChanged?.Invoke();
        });
    }
}
