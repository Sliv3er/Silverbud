using System;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Silverbud.Core;

public enum WatchList
{
    /// <summary>Paired classic Bluetooth devices: the entry pairing, settings and RFCOMM work with.</summary>
    Paired,
    /// <summary>Unpaired classic devices found while searching.</summary>
    Nearby,
    /// <summary>Paired Bluetooth LE entries. Buds3 paired as LE Audio stream over these, so they say whether the buds are in use.</summary>
    PairedLe
}

/// <summary>
/// Watches paired Bluetooth devices continuously and scans for nearby unpaired ones on demand.
/// Every event says which list it belongs to, so a device moving from "nearby" to "paired" is never lost.
/// </summary>
public sealed class DeviceScanner : IDisposable
{
    public const string AddressKey = "System.Devices.Aep.DeviceAddress";
    public const string ConnectedKey = "System.Devices.Aep.IsConnected";
    public const string ContainerKey = "System.Devices.Aep.ContainerId";

    private static readonly string[] Properties = [AddressKey, ConnectedKey, ContainerKey, "System.Devices.Aep.IsPaired"];

    private DeviceWatcher? _paired;
    private DeviceWatcher? _pairedLe;
    private DeviceWatcher? _nearby;

    public event Action<DeviceInformation, WatchList>? Added;
    public event Action<DeviceInformationUpdate, WatchList>? Updated;
    public event Action<string, WatchList>? Removed;
    public event Action? PairedListReady;

    public void StartPaired()
    {
        _paired = Create(BluetoothDevice.GetDeviceSelectorFromPairingState(true), WatchList.Paired);
        _paired.EnumerationCompleted += (_, _) => PairedListReady?.Invoke();
        _paired.Start();
        _pairedLe = Create(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), WatchList.PairedLe);
        _pairedLe.Start();
    }

    public void StartNearby()
    {
        StopNearby();
        _nearby = Create(BluetoothDevice.GetDeviceSelectorFromPairingState(false), WatchList.Nearby);
        _nearby.Start();
    }

    public void StopNearby()
    {
        Stop(_nearby);
        _nearby = null;
    }

    private DeviceWatcher Create(string selector, WatchList list)
    {
        var watcher = DeviceInformation.CreateWatcher(selector, Properties, DeviceInformationKind.AssociationEndpoint);
        watcher.Added += (_, info) => Added?.Invoke(info, list);
        watcher.Updated += (_, update) => Updated?.Invoke(update, list);
        watcher.Removed += (_, update) => Removed?.Invoke(update.Id, list);
        return watcher;
    }

    private static void Stop(DeviceWatcher? watcher)
    {
        if (watcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted })
            watcher.Stop();
    }

    /// <summary>Pairs using Secure Simple Pairing; earbuds have no screen, so the request is accepted directly.</summary>
    public static async Task<DevicePairingResultStatus> PairAsync(DeviceInformation info)
    {
        var custom = info.Pairing.Custom;
        void Accept(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args) => args.Accept();
        custom.PairingRequested += Accept;
        try
        {
            var result = await custom.PairAsync(
                DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.DisplayPin,
                DevicePairingProtectionLevel.Default);
            return result.Status;
        }
        finally
        {
            custom.PairingRequested -= Accept;
        }
    }

    public void Dispose()
    {
        StopNearby();
        Stop(_paired);
        Stop(_pairedLe);
    }
}
