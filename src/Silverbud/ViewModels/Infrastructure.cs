using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Silverbud.Core;
using Windows.Devices.Enumeration;

namespace Silverbud.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    private bool _running;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        : this(p => { execute(p); return Task.CompletedTask; }, canExecute)
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await execute(parameter);
        }
        catch (Exception ex)
        {
            Log.Write($"Command failed: {ex}");
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

/// <summary>A Bluetooth device as shown in the side list.</summary>
public sealed class DeviceItem : Observable
{
    private string _name = "";
    private bool _isPaired;
    private bool _isConnected;
    private bool _isLeConnected;
    private bool _isPairing;

    public DeviceItem(DeviceInformation info)
    {
        Info = info;
        Id = info.Id;
        Refresh();
    }

    /// <summary>Offline sample used by the --demo preview.</summary>
    public DeviceItem(string name, string address, bool paired)
    {
        Id = "demo:" + address;
        _name = name;
        Address = address;
        _isPaired = paired;
        _isConnected = paired;
    }

    public bool IsDemo => Id.StartsWith("demo:", StringComparison.Ordinal);

    public DeviceInformation? Info { get; }
    public string Id { get; }
    public string Address { get; private set; } = "";
    public Guid? ContainerId { get; private set; }

    public string Name { get => _name; private set => Set(ref _name, value); }
    public bool IsPaired { get => _isPaired; private set => Set(ref _isPaired, value); }

    /// <summary>Classic Bluetooth link (A2DP/HFP audio, and the settings channel).</summary>
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (Set(ref _isConnected, value))
                Raise(nameof(IsOnline));
        }
    }

    /// <summary>The buds' LE Audio link (Windows 11 streams Buds3 over LE Audio when it can).</summary>
    public bool IsLeConnected
    {
        get => _isLeConnected;
        set
        {
            if (Set(ref _isLeConnected, value))
                Raise(nameof(IsOnline));
        }
    }

    /// <summary>Connected to this PC in any way, i.e. out of the case and in use.</summary>
    public bool IsOnline => IsConnected || IsLeConnected;
    public bool IsPairing { get => _isPairing; set => Set(ref _isPairing, value); }

    public bool IsGalaxyBuds => Name.Contains("Buds", StringComparison.OrdinalIgnoreCase);

    public string Model =>
        Name.Contains("Buds3 Pro", StringComparison.OrdinalIgnoreCase) ? "Galaxy Buds3 Pro" :
        Name.Contains("Buds3", StringComparison.OrdinalIgnoreCase) ? "Galaxy Buds3" :
        "Galaxy Buds";

    public void Update(DeviceInformationUpdate update)
    {
        Info?.Update(update);
        Refresh();
    }

    private void Refresh()
    {
        if (Info is null)
            return;
        Name = Info.Name;
        IsPaired = Info.Pairing.IsPaired;
        if (Info.Properties.TryGetValue(DeviceScanner.AddressKey, out var a) && a is string address)
            Address = address.ToUpperInvariant();
        if (Info.Properties.TryGetValue(DeviceScanner.ConnectedKey, out var c) && c is bool connected)
            IsConnected = connected;
        if (Info.Properties.TryGetValue(DeviceScanner.ContainerKey, out var g) && g is Guid container)
            ContainerId = container;
    }
}

public sealed class EqBand(string label, int gain) : Observable
{
    private int _gain = gain;
    public string Label { get; } = label;
    public int Gain { get => _gain; set => Set(ref _gain, Math.Clamp(value, -10, 10)); }
}
