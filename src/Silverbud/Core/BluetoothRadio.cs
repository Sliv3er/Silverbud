using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Radios;

namespace Silverbud.Core;

/// <summary>The PC's Bluetooth radio: whether it's on, and turning it on (as the Bluetooth switch in Windows Settings does).</summary>
public sealed class BluetoothRadio
{
    private Radio? _radio;

    /// <summary>Raised with true when Bluetooth turns on, false when it turns off.</summary>
    public event Action<bool>? StateChanged;

    public bool IsAvailable => _radio is not null;
    public bool IsOn => _radio?.State == RadioState.On;

    public async Task InitAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            var radios = await Radio.GetRadiosAsync();
            _radio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            Log.Write($"Bluetooth radio: access {access}, state {_radio?.State.ToString() ?? "no adapter"}");
            if (_radio is not null)
                _radio.StateChanged += (radio, _) => StateChanged?.Invoke(radio.State == RadioState.On);
        }
        catch (Exception ex)
        {
            Log.Write($"Bluetooth radio unavailable: {ex.Message}");
        }
    }

    /// <returns>False when Windows doesn't let apps control radios (Settings › Privacy › Radios).</returns>
    public async Task<bool> TurnOnAsync()
    {
        if (_radio is null)
            return false;
        if (_radio.State == RadioState.On)
            return true;
        try
        {
            var result = await _radio.SetStateAsync(RadioState.On);
            Log.Write($"Turn Bluetooth on: {result}");
            return result == RadioAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            Log.Write($"Turn Bluetooth on failed: {ex.Message}");
            return false;
        }
    }
}
