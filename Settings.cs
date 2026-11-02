using System.Text.Json;
using System.Text.Json.Serialization;

namespace Switch2Bridge;

sealed class ControllerPrefs
{
    public string? Name { get; set; }
    public int? Player { get; set; }
    public bool Sideways { get; set; }
}

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

    /// <summary>Keep a local log of controller inputs for debugging (last 7 days).</summary>
    public bool LogInputs { get; set; } = true;

    /// <summary>A release the user chose to skip, e.g. "0.6.0".</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>Remembered per controller, keyed by its Bluetooth address in hex.</summary>
    public Dictionary<string, ControllerPrefs> Controllers { get; set; } = [];

    [JsonIgnore] public bool ExistedOnLoad { get; private set; }

    static string Key(ulong address) => address.ToString("X12");

    public static string? ControllerName(ulong address)
    {
        lock (Gate) return Current.Controllers.GetValueOrDefault(Key(address))?.Name;
    }

    public static bool ControllerSideways(ulong address)
    {
        lock (Gate) return Current.Controllers.GetValueOrDefault(Key(address))?.Sideways == true;
    }

    public static void SetControllerSideways(ulong address, bool sideways)
    {
        lock (Gate) Prefs(address).Sideways = sideways;
        Save();
    }

    public static int? ControllerPlayer(ulong address)
    {
        lock (Gate) return Current.Controllers.GetValueOrDefault(Key(address))?.Player;
    }

    /// <summary>Player numbers remembered for controllers other than this one.</summary>
    public static HashSet<int> PlayersReservedByOthers(ulong address)
    {
        lock (Gate)
            return Current.Controllers.Where(c => c.Key != Key(address) && c.Value.Player != null)
                .Select(c => c.Value.Player!.Value).ToHashSet();
    }

    public static void SetControllerName(ulong address, string name)
    {
        lock (Gate) Prefs(address).Name = name.Length > 0 ? name : null;
        Save();
    }

    public static void RememberPlayer(ulong address, int player)
    {
        lock (Gate) Prefs(address).Player = player;
        Save();
    }

    static ControllerPrefs Prefs(ulong address)
    {
        if (!Current.Controllers.TryGetValue(Key(address), out var prefs)) Current.Controllers[Key(address)] = prefs = new();
        return prefs;
    }

    /// <summary>The settings as JSON without per-controller data, for a diagnostics report.</summary>
    public static string ForDiagnostics()
    {
        lock (Gate)
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Current, Json))!.AsObject();
            json.Remove("controllers");
            return json.ToJsonString(Json);
        }
    }

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
