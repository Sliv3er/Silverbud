using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Silverbud.Core;

/// <summary>
/// Buds paired as LE Audio have no classic audio driver to send a connect request to. Asking Windows to
/// hold an LE connection to each bud brings them back, after which Windows' LE Audio stack restores the
/// sound by itself — the same thing that happens when you select Connect in Windows Settings.
/// </summary>
public sealed class LeAudioWaker : IDisposable
{
    private readonly List<GattSession> _sessions = new();
    private readonly List<BluetoothLEDevice> _devices = new();

    public async Task WakeAsync(IEnumerable<string> leDeviceIds)
    {
        foreach (var id in leDeviceIds)
        {
            try
            {
                var device = await BluetoothLEDevice.FromIdAsync(id);
                if (device is null)
                    continue;
                _devices.Add(device);
                var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
                session.MaintainConnection = true;
                _sessions.Add(session);
                // Touching GATT makes Windows connect now rather than whenever it next scans
                var services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                Log.Write($"LE wake {device.Name} ({id[^17..]}): {services.Status}, {device.ConnectionStatus}");
            }
            catch (Exception ex)
            {
                Log.Write($"LE wake failed for {id}: {ex.Message}");
            }
        }
    }

    /// <summary>Lets Windows' audio stack own the connection again.</summary>
    public void Dispose()
    {
        foreach (var session in _sessions)
        {
            try
            {
                session.MaintainConnection = false;
                session.Dispose();
            }
            catch { }
        }
        foreach (var device in _devices)
        {
            try { device.Dispose(); } catch { }
        }
        _sessions.Clear();
        _devices.Clear();
    }
}
