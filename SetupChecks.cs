using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace Switch2Bridge;

/// <summary>Spots common setup problems, so the status window can say what to fix.</summary>
static class SetupChecks
{
    internal sealed record Facts(bool? BluetoothOn, bool SteamRunning, IReadOnlyList<string> OtherApps, IReadOnlyList<string> PairedInWindows, bool AnyConnected);

    // Other tools that also read or remap controllers.
    static readonly string[] OtherControllerApps = ["DS4Windows", "BetterJoy", "BetterJoyForCemu", "x360ce", "reWASD", "switch2pc"];

    static readonly string[] ControllerNameHints = ["Pro Controller", "Joy-Con", "Joycon", "Nintendo"];

    /// <summary>Plain-language warnings for what was found; empty when all looks fine.</summary>
    internal static List<string> Evaluate(Facts facts)
    {
        var warnings = new List<string>();
        if (facts.BluetoothOn == false) warnings.Add("Bluetooth is turned off. Turn it on in Windows settings.");
        if (!facts.AnyConnected)
            foreach (var name in facts.PairedInWindows)
                warnings.Add($"Windows has a paired device called \"{name}\". If that's a Switch 2 controller, remove it in Windows Bluetooth settings, because the bridge connects to it directly.");
        if (!facts.SteamRunning) warnings.Add("Steam isn't running. Open it to use your controllers in games.");
        foreach (var app in facts.OtherApps)
            warnings.Add($"{app} is running and may also use your controller. Close it if inputs act strangely.");
        return warnings;
    }

    public static async Task<Facts> GatherAsync(bool anyConnected)
    {
        bool? bluetoothOn = null;
        var paired = new List<string>();
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter != null)
            {
                var radio = await adapter.GetRadioAsync();
                if (radio != null) bluetoothOn = radio.State == RadioState.On;
            }
            foreach (var selector in new[] { BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), BluetoothDevice.GetDeviceSelectorFromPairingState(true) })
                foreach (var device in await DeviceInformation.FindAllAsync(selector))
                    if (ControllerNameHints.Any(h => device.Name.Contains(h, StringComparison.OrdinalIgnoreCase)) && !paired.Contains(device.Name))
                        paired.Add(device.Name);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warn($"Setup check couldn't read Bluetooth state: {e.Message}");
        }

        var running = Process.GetProcesses().Select(p => p.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new Facts(bluetoothOn, running.Contains("steam"), OtherControllerApps.Where(running.Contains).ToList(), paired, anyConnected);
    }

    /// <summary>The adapter's name and abilities for a diagnostics report, without its address.</summary>
    public static async Task<string> DescribeAdapterAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter == null) return "none found";
            var info = await DeviceInformation.CreateFromIdAsync(adapter.DeviceId);
            return $"{info.Name}, Bluetooth LE {adapter.IsLowEnergySupported}, central role {adapter.IsCentralRoleSupported}";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"unknown ({e.Message})";
        }
    }
}
