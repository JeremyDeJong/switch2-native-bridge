using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Switch2Bridge;

enum ControllerKind { ProController, JoyConLeft, JoyConRight }

/// <summary>
/// A Switch 2 Pro Controller or Joy-Con connected over Bluetooth LE. Talks the
/// controller's GATT command protocol (as documented by switch2_controller_research
/// and used by switch2controllerpc) and surfaces its raw input reports untouched.
/// </summary>
sealed class SwitchController : IAsyncDisposable
{
    public const ushort NintendoVid = 0x057E;
    public const ushort ProController2Pid = 0x2069;
    public const ushort JoyConRightPid = 0x2066;
    public const ushort JoyConLeftPid = 0x2067;

    public static ControllerKind? KindOf(ushort pid) => pid switch
    {
        ProController2Pid => ControllerKind.ProController,
        JoyConLeftPid => ControllerKind.JoyConLeft,
        JoyConRightPid => ControllerKind.JoyConRight,
        _ => null,
    };

    static readonly Guid InputReportUuid = Guid.Parse("ab7de9be-89fe-49ad-828f-118f09df7fd2");
    static readonly Guid VibrationProUuid = Guid.Parse("cc483f51-9258-427d-a939-630c31f72b05");
    static readonly Guid VibrationJoyConLeftUuid = Guid.Parse("289326cb-a471-485d-a8f4-240c14f18241");
    static readonly Guid VibrationJoyConRightUuid = Guid.Parse("fa19b0fb-cd1f-46a7-84a1-bbb09e00c149");
    static readonly Guid CommandWriteUuid = Guid.Parse("649d4ac9-8eb7-4e6c-af44-1ea54fe5f005");
    static readonly Guid CommandResponseUuid = Guid.Parse("c765a961-d9d8-4d36-a20a-5315b111836a");

    const byte CmdMemory = 0x02, SubMemoryRead = 0x04;
    const byte CmdLeds = 0x09, SubLedsSetPlayer = 0x07;
    const byte CmdFeature = 0x0C, SubFeatureInit = 0x02, SubFeatureEnable = 0x04;
    const byte CmdPair = 0x15, SubPairSetMac = 0x01, SubPairLtk1 = 0x04, SubPairLtk2 = 0x02, SubPairFinish = 0x03;
    const byte FeatureMotion = 0x04;

    readonly BluetoothLEDevice _device;
    readonly GattSession _session;
    readonly IDisposable? _connectionParams;
    readonly GattCharacteristic _input, _cmdWrite, _cmdResponse;
    readonly Dictionary<Guid, GattCharacteristic> _vibration;
    readonly SemaphoreSlim _cmdLock = new(1, 1);
    TaskCompletionSource<byte[]>? _pendingResponse;
    int _disconnected;

    /// <summary>Raised for every input notification, with the raw report bytes.</summary>
    public event Action<byte[]>? InputReport;
    public event Action? Disconnected;

    public ulong Address => _device.BluetoothAddress;
    public ControllerKind Kind { get; private set; }
    public string Name => Kind switch
    {
        ControllerKind.JoyConLeft => "Joy-Con (L)",
        ControllerKind.JoyConRight => "Joy-Con (R)",
        _ => "Pro Controller 2",
    };

    SwitchController(BluetoothLEDevice device, GattSession session, IDisposable? connectionParams,
        GattCharacteristic input, GattCharacteristic cmdWrite, GattCharacteristic cmdResponse,
        Dictionary<Guid, GattCharacteristic> vibration)
    {
        _device = device;
        _session = session;
        _connectionParams = connectionParams;
        _input = input;
        _cmdWrite = cmdWrite;
        _cmdResponse = cmdResponse;
        _vibration = vibration;
        _device.ConnectionStatusChanged += (d, _) =>
        {
            if (d.ConnectionStatus == BluetoothConnectionStatus.Disconnected) RaiseDisconnected();
        };
        _session.SessionStatusChanged += (_, e) =>
        {
            if (e.Status == GattSessionStatus.Closed) RaiseDisconnected();
        };
    }

    void RaiseDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) == 0) Disconnected?.Invoke();
    }

    /// <summary>Connects, finds the GATT characteristics and reads which controller this is.</summary>
    public static async Task<SwitchController> ConnectAsync(ulong address, CancellationToken ct)
    {
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct)
            ?? throw new InvalidOperationException($"Could not open Bluetooth device {Format(address)}.");

        // Ask Windows to keep the link up and to use the fastest connection
        // interval it allows; both matter for a 4 ms input stream.
        var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(ct);
        session.MaintainConnection = true;
        IDisposable? connectionParams = null;
        try
        {
            connectionParams = device.RequestPreferredConnectionParameters(
                BluetoothLEPreferredConnectionParameters.ThroughputOptimized);
        }
        catch (Exception e)
        {
            Log.Warn($"Could not request throughput-optimized connection: {e.Message}");
        }

        GattCharacteristic? input = null, cmdWrite = null, cmdResponse = null;
        var vibration = new Dictionary<Guid, GattCharacteristic>();
        for (int attempt = 1; attempt <= 3 && (input == null || cmdWrite == null || cmdResponse == null); attempt++)
        {
            var services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(ct);
            if (services.Status != GattCommunicationStatus.Success)
            {
                Log.Warn($"GATT service discovery failed ({services.Status}), retry {attempt}/3");
                await Task.Delay(1000, ct);
                continue;
            }
            foreach (var service in services.Services)
            {
                var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(ct);
                if (chars.Status != GattCommunicationStatus.Success) continue;
                foreach (var c in chars.Characteristics)
                {
                    if (c.Uuid == InputReportUuid) input = c;
                    else if (c.Uuid == CommandWriteUuid) cmdWrite = c;
                    else if (c.Uuid == CommandResponseUuid) cmdResponse = c;
                    else if (c.Uuid == VibrationProUuid || c.Uuid == VibrationJoyConLeftUuid || c.Uuid == VibrationJoyConRightUuid)
                        vibration[c.Uuid] = c;
                }
            }
        }
        if (input == null || cmdWrite == null || cmdResponse == null)
        {
            session.Dispose();
            device.Dispose();
            throw new InvalidOperationException("The controller did not expose the expected GATT characteristics.");
        }

        var controller = new SwitchController(device, session, connectionParams, input, cmdWrite, cmdResponse, vibration);
        try
        {
            cmdResponse.ValueChanged += (_, e) => controller._pendingResponse?.TrySetResult(e.CharacteristicValue.ToArray());
            await EnableNotify(cmdResponse, ct);

            var info = await controller.ReadMemoryAsync(0x13000, 0x40, ct);
            var pid = BitConverter.ToUInt16(info, 20);
            controller.Kind = KindOf(pid)
                ?? throw new InvalidOperationException($"Unsupported controller (product 0x{pid:X4}).");
            return controller;
        }
        catch
        {
            await controller.DisposeAsync();
            throw;
        }
    }

    static async Task EnableNotify(GattCharacteristic c, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            var status = await c.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct);
            if (status == GattCommunicationStatus.Success) return;
            if (attempt == 3) throw new InvalidOperationException($"Enabling notifications on {c.Uuid} failed: {status}");
            await Task.Delay(1000, ct);
        }
    }

    static GattWriteOption WriteOptionFor(GattCharacteristic c) =>
        c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;

    async Task<byte[]> CommandAsync(byte command, byte subcommand, byte[] data, CancellationToken ct)
    {
        var buffer = new byte[8 + data.Length];
        buffer[0] = command;
        buffer[1] = 0x91;
        buffer[2] = 0x01;
        buffer[3] = subcommand;
        buffer[5] = (byte)data.Length;
        data.CopyTo(buffer, 8);

        await _cmdLock.WaitAsync(ct);
        try
        {
            _pendingResponse = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var status = await _cmdWrite.WriteValueAsync(buffer.AsBuffer(), WriteOptionFor(_cmdWrite)).AsTask(ct);
            if (status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"Command {command:X2}/{subcommand:X2} write failed: {status}");
            var response = await _pendingResponse.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
            if (response.Length < 8 || response[0] != command || response[1] != 0x01)
                throw new InvalidOperationException($"Unexpected response to {command:X2}/{subcommand:X2}: {Convert.ToHexString(response)}");
            return response[8..];
        }
        finally
        {
            _pendingResponse = null;
            _cmdLock.Release();
        }
    }

    public async Task<byte[]> ReadMemoryAsync(uint address, byte length, CancellationToken ct)
    {
        if (length > 0x4F) throw new ArgumentOutOfRangeException(nameof(length), "Maximum read size is 0x4F bytes.");
        var request = new byte[8];
        request[0] = length;
        request[1] = 0x7E;
        BitConverter.TryWriteBytes(request.AsSpan(4), address);
        var data = await CommandAsync(CmdMemory, SubMemoryRead, request, ct);
        if (data.Length < 8 + length || data[0] != length || BitConverter.ToUInt32(data, 4) != address)
            throw new InvalidOperationException($"Unexpected memory read reply for 0x{address:X}: {Convert.ToHexString(data)}");
        return data[8..(8 + length)];
    }

    /// <summary>Reads a flash range in chunks the controller accepts.</summary>
    public async Task<byte[]> ReadFlashAsync(uint address, int length, CancellationToken ct)
    {
        var result = new byte[length];
        for (int offset = 0; offset < length; offset += 0x40)
        {
            var chunk = (byte)Math.Min(0x40, length - offset);
            var data = await ReadMemoryAsync(address + (uint)offset, chunk, ct);
            data.CopyTo(result, offset);
        }
        return result;
    }

    /// <summary>Stores this PC's Bluetooth address and pairing keys on the controller,
    /// so it reconnects here when powered on. Same sequence switch2controllerpc uses.</summary>
    public async Task PairAsync(ulong hostAddress, CancellationToken ct)
    {
        var mac = BitConverter.GetBytes(hostAddress)[..6];
        await CommandAsync(CmdPair, SubPairSetMac, [0x00, 0x02, .. mac, .. mac], ct);
        await CommandAsync(CmdPair, SubPairLtk1,
            [0x00, 0xea, 0xbd, 0x47, 0x13, 0x89, 0x35, 0x42, 0xc6, 0x79, 0xee, 0x07, 0xf2, 0x53, 0x2c, 0x6c, 0x31], ct);
        await CommandAsync(CmdPair, SubPairLtk2,
            [0x00, 0x40, 0xb0, 0x8a, 0x5f, 0xcd, 0x1f, 0x9b, 0x41, 0x12, 0x5c, 0xac, 0xc6, 0x3f, 0x38, 0xa0, 0x73], ct);
        await CommandAsync(CmdPair, SubPairFinish, [0x00], ct);
    }

    public async Task StartInputAsync(CancellationToken ct)
    {
        _input.ValueChanged += (_, e) => InputReport?.Invoke(e.CharacteristicValue.ToArray());
        await EnableNotify(_input, ct);
        await CommandAsync(CmdFeature, SubFeatureInit, [FeatureMotion, 0, 0, 0], ct);
        await CommandAsync(CmdFeature, SubFeatureEnable, [FeatureMotion, 0, 0, 0], ct);
    }

    public Task SetPlayerLedsAsync(byte pattern, CancellationToken ct) =>
        CommandAsync(CmdLeds, SubLedsSetPlayer, [pattern, 0, 0, 0], ct);

    /// <summary>Writes rumble in the Bluetooth framing: a 0x00 prefix, then motor blocks
    /// of 16 bytes each, byte-for-byte the blocks a host puts after report ID 0x02 when it
    /// rumbles a wired pad (left then right). A Joy-Con takes only its own side's block.</summary>
    public async Task WriteRumbleAsync(ReadOnlyMemory<byte> leftAndRight)
    {
        var (uuid, block) = Kind switch
        {
            ControllerKind.JoyConLeft => (VibrationJoyConLeftUuid, leftAndRight[..16]),
            ControllerKind.JoyConRight => (VibrationJoyConRightUuid, leftAndRight[16..32]),
            _ => (VibrationProUuid, leftAndRight[..32]),
        };
        if (!_vibration.TryGetValue(uuid, out var characteristic)) return;
        var packet = new byte[1 + block.Length];
        block.Span.CopyTo(packet.AsSpan(1));
        await characteristic.WriteValueAsync(packet.AsBuffer(), WriteOptionFor(characteristic));
    }

    public static string Format(ulong address) =>
        string.Join(":", BitConverter.GetBytes(address)[..6].Reverse().Select(b => b.ToString("X2")));

    public ValueTask DisposeAsync()
    {
        _connectionParams?.Dispose();
        try { _session.MaintainConnection = false; } catch (ObjectDisposedException) { }
        _session.Dispose();
        _device.Dispose();
        return ValueTask.CompletedTask;
    }
}
