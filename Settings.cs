using System.Text.Json;
using System.Text.Json.Serialization;

namespace Switch2Bridge;

/// <summary>The bridge's preferences, kept in %LOCALAPPDATA%\Switch2Bridge\settings.json.</summary>
sealed class Settings
{
    static readonly string FilePath = Path.Combine(Log.Directory, "settings.json");
    static readonly object Gate = new();
    // camelCase, as the 0.2-0.5 file ({"autostartConfigured":true}) was written.
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static Settings Current { get; private set; } = new();

    /// <summary>True once start-with-Windows has been set up on first run.</summary>
    public bool AutostartConfigured { get; set; }

    /// <summary>0 to 1: scales the rumble amplitudes Steam sends.</summary>
    public double RumbleStrength { get; set; } = 1.0;

    /// <summary>Turn a controller off after this many minutes without input; 0 = never.</summary>
    public int IdleOffMinutes { get; set; } = 15;

    public bool OffOnSleep { get; set; } = true;
    public bool OffOnQuit { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>A release the user chose to skip, e.g. "0.6.0".</summary>
    public string? SkippedVersion { get; set; }

    [JsonIgnore] public bool ExistedOnLoad { get; private set; }

    public static void Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    Current = FromJson(File.ReadAllText(FilePath));
                    Current.ExistedOnLoad = true;
                }
            }
            catch (Exception e)
            {
                Log.Warn($"Settings file unreadable, using defaults: {e.Message}");
                Current = new();
            }
            Current.RumbleStrength = Math.Clamp(Current.RumbleStrength, 0, 1);
            Current.IdleOffMinutes = Math.Max(0, Current.IdleOffMinutes);
        }
    }

    internal static Settings FromJson(string json) => JsonSerializer.Deserialize<Settings>(json, Json) ?? new();

    public static void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Log.Directory);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Json));
            }
            catch (Exception e)
            {
                Log.Warn($"Could not save settings: {e.Message}");
            }
        }
    }
}
