using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using Silverbud.Core;
using Windows.Devices.Enumeration;

namespace Silverbud.ViewModels;

public enum LinkState { Idle, Connecting, Connected }

public sealed class MainViewModel : Observable, IDisposable
{
    private const int CustomEqPreset = 6;
    private static readonly string[] BandLabels = ["63", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"];

    private readonly Dispatcher _ui;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DeviceScanner _scanner = new();
    private readonly Dictionary<string, DeviceItem> _pairedById = new();
    private readonly Dictionary<string, DeviceItem> _nearbyById = new();
    private readonly Dictionary<string, DeviceItem> _leById = new();
    private readonly Dictionary<string, CancellationTokenSource> _debounce = new();

    private BudsLink? _link;
    private Task _sendChain = Task.CompletedTask;
    private bool _opening;
    private bool _syncing;
    private bool _demo;
    private bool _userDisconnected;
    private bool _selectedOnline;
    private bool _customEqPushed;
    private bool _lowAlerted;
    private string? _selectAfterPairing;
    private CancellationTokenSource? _scanTimer;

    public MainViewModel(Dispatcher ui)
    {
        _ui = ui;
        EqBands = new ObservableCollection<EqBand>(BandLabels.Select((label, i) => new EqBand(label, _settings.CustomEq[i])));
        foreach (var band in EqBands)
            band.PropertyChanged += (_, _) => OnBandChanged();
        NearbyDevices.CollectionChanged += (_, _) => Raise(nameof(ShowNearby));

        ScanCommand = new RelayCommand(_ => ToggleScan());
        // Demo devices keep their buttons enabled so the --demo preview looks like the real thing; the actions no-op.
        PairCommand = new RelayCommand(PairAsync, p => p is DeviceItem { IsPairing: false });
        ConnectCommand = new RelayCommand(_ => ConnectAsync(), _ => Selected is not null && _link is null && !_opening);
        DisconnectCommand = new RelayCommand(_ => DisconnectAsync(), _ => Selected is not null && (_link is not null || Selected.IsOnline));
        ForgetCommand = new RelayCommand(_ => ForgetAsync(), _ => Selected is not null);
        RingCommand = new RelayCommand(_ => ToggleRing(), _ => IsLinked);
        PairingModeCommand = new RelayCommand(_ => EnterPairingMode(), _ => IsLinked);
        FactoryResetCommand = new RelayCommand(_ => FactoryReset(), _ => IsLinked);
        RefreshInfoCommand = new RelayCommand(_ => Send(MsgId.DebugGetAllData), _ => IsLinked);
        ResetEqCommand = new RelayCommand(_ => { foreach (var b in EqBands) b.Gain = 0; });
        OpenBluetoothSettingsCommand = new RelayCommand(_ => Shell("ms-settings:bluetooth"));
        OpenLogFolderCommand = new RelayCommand(_ => Shell(AppSettings.Folder));
    }

    // ───── Wiring to the window ─────

    public Func<string, string, bool>? Confirm { get; set; }
    public event Action<string, string>? Notify;
    public event Action? TrayTextChanged;

    public ObservableCollection<DeviceItem> PairedDevices { get; } = new();
    public ObservableCollection<DeviceItem> NearbyDevices { get; } = new();
    public ObservableCollection<EqBand> EqBands { get; }

    public ICommand ScanCommand { get; }
    public ICommand PairCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ForgetCommand { get; }
    public ICommand RingCommand { get; }
    public ICommand PairingModeCommand { get; }
    public ICommand FactoryResetCommand { get; }
    public ICommand RefreshInfoCommand { get; }
    public ICommand ResetEqCommand { get; }
    public ICommand OpenBluetoothSettingsCommand { get; }
    public ICommand OpenLogFolderCommand { get; }

    public void Start()
    {
        _scanner.Added += (info, list) => _ui.BeginInvoke(() => OnAdded(info, list));
        _scanner.Updated += (update, list) => _ui.BeginInvoke(() => OnUpdated(update, list));
        _scanner.Removed += (id, list) => _ui.BeginInvoke(() => OnRemoved(id, list));
        _scanner.PairedListReady += () => _ui.BeginInvoke(() =>
        {
            ListReady = true;
            Selected ??= PairedDevices.FirstOrDefault();
        });
        try
        {
            _scanner.StartPaired();
        }
        catch (Exception ex)
        {
            Log.Write($"Bluetooth watcher failed: {ex}");
            RailMessage = "Bluetooth isn't available. Turn it on in Windows settings.";
        }
    }

    // ───── Device list ─────

    private DeviceItem? _selected;
    private bool _listReady;
    private bool _isScanning;
    private string _railMessage = "";

    public DeviceItem? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;
            _selected = value;
            _selectedOnline = value?.IsOnline == true;
            Raise();
            Raise(nameof(HasSelection));
            RaiseConnectionState();
            _ = OnSelectionChangedAsync(value);
        }
    }

    public bool HasSelection => _selected is not null;
    public bool ListReady { get => _listReady; private set { if (Set(ref _listReady, value)) Raise(nameof(ShowNoPairedHint)); } }
    public bool ShowNoPairedHint => ListReady && PairedDevices.Count == 0;
    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!Set(ref _isScanning, value))
                return;
            Raise(nameof(ScanLabel));
        }
    }

    public bool ShowNearby => NearbyDevices.Count > 0;
    public string ScanLabel => IsScanning ? "Stop searching" : "Find buds to pair";
    public string RailMessage { get => _railMessage; private set => Set(ref _railMessage, value); }

    private void OnAdded(DeviceInformation info, WatchList list)
    {
        var item = new DeviceItem(info);
        if (list == WatchList.PairedLe)
        {
            _leById[item.Id] = item;
            RefreshLeLinks();
            return;
        }
        if (list == WatchList.Paired)
        {
            Replace(_pairedById, PairedDevices, item);
            if (_nearbyById.Remove(item.Id, out var stale))
                NearbyDevices.Remove(stale);
            Raise(nameof(ShowNoPairedHint));
            RefreshLeLinks();

            if (item.Id == _selectAfterPairing)
            {
                _selectAfterPairing = null;
                Selected = item;
                _ = ConnectAfterPairingAsync(item);
            }
            else if (Selected is null && item.Id == _settings.LastDeviceId)
            {
                Selected = item;
            }
        }
        else
        {
            _nearbyById[item.Id] = item;
            PlaceNearby(item);
        }
    }

    private void OnUpdated(DeviceInformationUpdate update, WatchList list)
    {
        var map = list switch
        {
            WatchList.Paired => _pairedById,
            WatchList.PairedLe => _leById,
            _ => _nearbyById
        };
        if (!map.TryGetValue(update.Id, out var item))
            return;
        item.Update(update);
        switch (list)
        {
            case WatchList.Nearby:
                PlaceNearby(item);
                break;
            case WatchList.PairedLe:
                RefreshLeLinks();
                break;
            default:
                CheckSelectedOnline();
                break;
        }
    }

    private void OnRemoved(string id, WatchList list)
    {
        switch (list)
        {
            case WatchList.Paired when _pairedById.Remove(id, out var item):
                PairedDevices.Remove(item);
                Raise(nameof(ShowNoPairedHint));
                if (Selected == item)
                    Selected = PairedDevices.FirstOrDefault();
                break;
            case WatchList.PairedLe when _leById.Remove(id):
                RefreshLeLinks();
                break;
            case WatchList.Nearby when _nearbyById.Remove(id, out var item):
                NearbyDevices.Remove(item);
                break;
        }
    }

    /// <summary>
    /// Buds paired as LE Audio stream over separate LE entries (one per bud, the primary sharing the
    /// classic address). Mirror their connection onto the classic entry the app works with.
    /// </summary>
    private void RefreshLeLinks()
    {
        foreach (var buds in _pairedById.Values)
            buds.IsLeConnected = _leById.Values.Any(le => le.IsConnected &&
                (le.Address == buds.Address || le.Name == buds.Name));
        CheckSelectedOnline();
    }

    /// <summary>Reacts when the selected buds come out of the case (or go back in).</summary>
    private void CheckSelectedOnline()
    {
        var item = Selected;
        bool online = item?.IsOnline == true;
        if (online == _selectedOnline)
            return;
        _selectedOnline = online;
        RaiseConnectionState();
        if (item is null)
            return;
        if (online)
        {
            if (_settings.AutoConnect && _link is null && !_userDisconnected)
                _ = OpenLinkAfterDelayAsync(item, 1500);
        }
        else
        {
            _userDisconnected = false;
            if (_link is null)
                StatusText = "";
        }
    }

    private static void Replace(Dictionary<string, DeviceItem> map, ObservableCollection<DeviceItem> list, DeviceItem item)
    {
        if (map.Remove(item.Id, out var old))
            list.Remove(old);
        map[item.Id] = item;
        if (item.IsGalaxyBuds)
            list.Add(item);
    }

    private void PlaceNearby(DeviceItem item)
    {
        bool show = item.IsGalaxyBuds && !_pairedById.ContainsKey(item.Id);
        if (show && !NearbyDevices.Contains(item))
            NearbyDevices.Add(item);
        else if (!show)
            NearbyDevices.Remove(item);
    }

    private void ToggleScan()
    {
        if (IsScanning)
        {
            StopScan();
            return;
        }
        _nearbyById.Clear();
        NearbyDevices.Clear();
        try
        {
            _scanner.StartNearby();
        }
        catch (Exception ex)
        {
            Log.Write($"Scan failed: {ex}");
            RailMessage = "Bluetooth isn't available. Turn it on in Windows settings.";
            return;
        }
        IsScanning = true;
        RailMessage = "Searching… Open the case with the buds inside and keep it close to the PC.";
        _scanTimer = new CancellationTokenSource();
        _ = StopScanLaterAsync(_scanTimer.Token);
    }

    private async Task StopScanLaterAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(40000, token);
            StopScan();
        }
        catch (TaskCanceledException)
        {
        }
    }

    private void StopScan()
    {
        if (!IsScanning)
            return;
        _scanTimer?.Cancel();
        _scanner.StopNearby();
        IsScanning = false;
        RailMessage = NearbyDevices.Count > 0
            ? "Select Pair next to your buds."
            : "No buds in pairing mode were found. If they're connected to your phone, turn on pairing mode from the phone's Galaxy Wearable app, then search again.";
    }

    private async Task PairAsync(object? parameter)
    {
        if (parameter is not DeviceItem { Info: not null } item)
            return;
        StopScan();
        item.IsPairing = true;
        RailMessage = $"Pairing with {item.Name}…";
        try
        {
            var status = await DeviceScanner.PairAsync(item.Info);
            Log.Write($"Pairing {item.Name}: {status}");
            if (status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired)
            {
                RailMessage = $"Paired with {item.Name}. Windows is setting up the audio…";
                _selectAfterPairing = item.Id;
                if (_pairedById.TryGetValue(item.Id, out var existing))
                {
                    _selectAfterPairing = null;
                    Selected = existing;
                    _ = ConnectAfterPairingAsync(existing);
                }
            }
            else
            {
                RailMessage = status switch
                {
                    DevicePairingResultStatus.AuthenticationTimeout or DevicePairingResultStatus.AuthenticationFailure =>
                        "Pairing didn't finish in time. Put the buds back in pairing mode and try again.",
                    DevicePairingResultStatus.AccessDenied =>
                        "Windows blocked the pairing. Check Settings › Bluetooth & devices and try again.",
                    _ => $"Pairing failed ({status}). Put the buds back in pairing mode and try again."
                };
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Pairing error: {ex}");
            RailMessage = $"Pairing failed: {ex.Message}";
        }
        finally
        {
            item.IsPairing = false;
        }
    }

    private async Task ConnectAfterPairingAsync(DeviceItem item)
    {
        // Windows installs the audio drivers right after pairing; give it a moment first.
        await Task.Delay(4000);
        if (Selected == item && _link is null)
            await ConnectAsync();
        RailMessage = "";
    }

    private async Task ForgetAsync()
    {
        var item = Selected;
        if (item?.Info is null)
            return;
        if (Confirm?.Invoke("Forget these buds?",
                $"{item.Name} will be removed from this PC. To use them here again, you'll need to pair them again.") != true)
            return;
        _userDisconnected = true;
        await CloseLinkAsync();
        var result = await item.Info.Pairing.UnpairAsync();
        StatusText = result.Status is DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired
            ? "Removed from this PC."
            : $"Couldn't remove the buds ({result.Status}). Try again from Bluetooth settings.";
    }

    // ───── Connection ─────

    private LinkState _linkState;
    private string _statusText = "";

    public LinkState LinkState
    {
        get => _linkState;
        private set
        {
            if (Set(ref _linkState, value))
                RaiseConnectionState();
        }
    }

    public bool IsLinked => LinkState == LinkState.Connected;
    public bool IsNotLinked => !IsLinked;
    public bool ShowDisconnect => IsLinked || Selected is { IsOnline: true };
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    /// <summary>on · busy · partial · off — drives the status pill colour.</summary>
    public string PillState => LinkState switch
    {
        LinkState.Connected => "on",
        LinkState.Connecting => "busy",
        _ => Selected is { IsOnline: true } ? "partial" : "off"
    };

    public string PillText => PillState switch
    {
        "on" => "Connected",
        "busy" => "Connecting",
        "partial" => "Audio only",
        _ => "Not connected"
    };

    private void RaiseConnectionState()
    {
        Raise(nameof(IsLinked));
        Raise(nameof(IsNotLinked));
        Raise(nameof(ShowDisconnect));
        Raise(nameof(PillState));
        Raise(nameof(PillText));
        RaiseTray();
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task OnSelectionChangedAsync(DeviceItem? device)
    {
        await CloseLinkAsync();
        _userDisconnected = false;
        StatusText = "";
        if (device is null || device.IsDemo)
            return;
        _settings.LastDeviceId = device.Id;
        _settings.Save();
        if (device.IsOnline && _settings.AutoConnect)
            await OpenLinkAsync(device, 3);
        else if (!device.IsOnline)
            StatusText = "Select Connect when the buds are out of the case.";
    }

    private async Task ConnectAsync()
    {
        var device = Selected;
        if (device is null || device.IsDemo || _link is not null)
            return;
        _userDisconnected = false;
        if (!device.IsOnline)
        {
            LinkState = LinkState.Connecting;
            StatusText = "Asking Windows to connect the audio…";
            int accepted = await AudioConnector.ConnectAsync(device.Address, device.ContainerId);
            Log.Write($"Audio connect accepted by {accepted} driver(s)");
            await Task.Delay(accepted > 0 ? 3000 : 300);
        }
        await OpenLinkAsync(device, 4);
    }

    private async Task OpenLinkAfterDelayAsync(DeviceItem device, int delay)
    {
        await Task.Delay(delay);
        if (Selected == device && _link is null && !_userDisconnected)
            await OpenLinkAsync(device, 3);
    }

    private async Task OpenLinkAsync(DeviceItem device, int attempts)
    {
        if (_opening || _link is not null)
            return;
        _opening = true;
        LinkState = LinkState.Connecting;
        StatusText = "Connecting to the buds…";
        Exception? last = null;
        try
        {
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                if (Selected != device)
                    break;
                var link = new BudsLink();
                link.FrameReceived += frame => _ui.BeginInvoke(() => OnFrame(link, frame));
                link.Disconnected += reason => _ui.BeginInvoke(() => OnLinkLost(link, reason));
                try
                {
                    await link.ConnectAsync(device.Id);
                    if (Selected != device)
                    {
                        await link.DisposeAsync();
                        break;
                    }
                    _link = link;
                    _sendChain = Task.CompletedTask;
                    _customEqPushed = false;
                    LinkState = LinkState.Connected;
                    StatusText = "";
                    Log.Write($"Connected to {device.Name} ({device.Address})");
                    Send(MsgId.ManagerInfo, 1, 1, 34);
                    _ = RequestDiagnosticsAsync(link);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Log.Write($"Connect attempt {attempt}/{attempts} failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
                    await link.DisposeAsync();
                    if (attempt < attempts)
                        await Task.Delay(1500);
                }
            }
            LinkState = LinkState.Idle;
            if (Selected == device)
                StatusText = BudsLink.Explain(last);
        }
        finally
        {
            _opening = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task RequestDiagnosticsAsync(BudsLink link)
    {
        await Task.Delay(900);
        if (_link == link)
            Send(MsgId.DebugGetAllData);
    }

    private void OnLinkLost(BudsLink link, string? reason)
    {
        if (link != _link)
            return;
        _link = null;
        Log.Write($"Link lost: {reason}");
        LinkState = LinkState.Idle;
        ClearDeviceState();
        if (_userDisconnected)
            return;
        StatusText = "The buds disconnected. They reconnect automatically when they're back in range.";
        if (Selected is { IsOnline: true } device && _settings.AutoConnect)
            _ = OpenLinkAfterDelayAsync(device, 3000);
    }

    private async Task DisconnectAsync()
    {
        var device = Selected;
        if (device is null || device.IsDemo)
            return;
        _userDisconnected = true;
        if (IsRinging)
            IsRinging = false;
        await _sendChain;
        await CloseLinkAsync();
        StatusText = "Disconnecting…";
        int accepted = await AudioConnector.DisconnectAsync(device.Address, device.ContainerId);
        StatusText = accepted > 0
            ? "Disconnected. Your buds are free to connect to your phone."
            : "Settings link closed. To drop the audio too, disconnect the buds in Bluetooth settings.";
    }

    private async Task CloseLinkAsync()
    {
        var link = _link;
        _link = null;
        LinkState = LinkState.Idle;
        ClearDeviceState();
        if (link is not null)
            await link.DisposeAsync();
    }

    private void Send(byte id, params byte[] payload)
    {
        var link = _link;
        if (link is null)
            return;
        var frame = new SppFrame(id, payload);
        _sendChain = _sendChain.ContinueWith(async _ =>
        {
            try
            {
                await link.SendAsync(frame);
                Log.Write($"-> {frame}");
            }
            catch (Exception ex)
            {
                Log.Write($"Send {frame} failed: {ex.Message}");
            }
        }, TaskScheduler.Default).Unwrap();
    }

    private void Debounce(string key, Action action, int milliseconds = 220)
    {
        if (_debounce.Remove(key, out var previous))
            previous.Cancel();
        var cts = new CancellationTokenSource();
        _debounce[key] = cts;
        Task.Delay(milliseconds, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled)
                return;
            _debounce.Remove(key);
            action();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ───── Incoming messages ─────

    private void OnFrame(BudsLink link, SppFrame frame)
    {
        if (link != _link)
            return;
        // 242/244 are firmware debug events the buds repeat every couple of seconds
        if (frame.Id is not (MsgId.DebugEvent or MsgId.DebugEventExtra))
            Log.Write(frame.Id == MsgId.Ack && frame.Payload.Length > 0
                ? $"<- ack #{frame.Payload[0]} [{Convert.ToHexString(frame.Payload.AsSpan(1))}]"
                : $"<- {frame}");
        var p = frame.Payload;
        try
        {
            switch (frame.Id)
            {
                case MsgId.ExtendedStatusUpdated:
                    ApplyExtended(StatusParser.ParseExtended(p));
                    Send(MsgId.ManagerInfo, 1, 1, 34);
                    break;
                case MsgId.StatusUpdated:
                    ApplyBattery(StatusParser.ParseStatus(p));
                    break;
                case MsgId.NoiseControlsUpdate when p.Length > 0:
                    Sync(() => NoiseControl = p[0]);
                    break;
                case MsgId.TouchUpdated when p.Length > 0:
                    Sync(() => TouchEnabled = p[0] == 0);
                    break;
                case MsgId.MuteEarbudStatusUpdated when p.Length > 1:
                    Sync(() =>
                    {
                        MuteLeft = p[0] == 1;
                        MuteRight = p[1] == 1;
                    });
                    break;
                case MsgId.FindMyEarbudsStop:
                    Sync(() => IsRinging = false);
                    break;
                case MsgId.DebugGetAllData:
                    ApplyDiagnostics(StatusParser.ParseDebugData(p));
                    break;
                case MsgId.Ack when p.Length > 1:
                    OnAcknowledged(p[0], p[1]);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't read {frame}: {ex.Message}");
        }
    }

    /// <summary>
    /// The buds answer simple settings with the value they actually applied, which can differ from the
    /// request (e.g. noise canceling is refused while only one bud is worn). Show what really happened.
    /// </summary>
    private void OnAcknowledged(byte id, byte applied)
    {
        switch (id)
        {
            case MsgId.NoiseControls when applied != NoiseControl:
                int requested = NoiseControl;
                Sync(() => NoiseControl = applied);
                bool oneBudWorn = (_placementL == Placement.Wearing) != (_placementR == Placement.Wearing);
                string mode = requested == 1 ? "Noise canceling" : "Ambient sound";
                StatusText = oneBudWorn && !NoiseControlsWithOneEarbud
                    ? $"{mode} needs both buds in your ears. To use it with one bud, turn on Noise controls with one earbud in the Device tab."
                    : $"The buds didn't switch to {mode.ToLowerInvariant()}.";
                break;
            case MsgId.Equalizer:
                Sync(() => EqPreset = applied is <= 5 ? applied : CustomEqPreset);
                break;
        }
    }

    private void Sync(Action apply)
    {
        _syncing = true;
        try
        {
            apply();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ApplyExtended(ExtendedStatus s)
    {
        Sync(() =>
        {
            NoiseControl = s.NoiseControl;
            AmbientVolume = Math.Clamp(s.AmbientVolume, 0, 2);
            AmbientCustomOn = s.AmbientCustomOn;
            AmbientCustomLeft = Math.Clamp(s.AmbientCustomL, 0, 2);
            AmbientCustomRight = Math.Clamp(s.AmbientCustomR, 0, 2);
            AmbientTone = Math.Clamp(s.AmbientTone, 0, 4);
            EqPreset = s.EqMode is >= 0 and <= 5 ? s.EqMode : CustomEqPreset;
            Balance = Math.Clamp(s.Balance, 0, 32);
            GameMode = s.GameMode;
            TouchEnabled = s.TouchEnabled;
            PinchOn = s.PinchOn;
            DoublePinchOn = s.DoublePinchOn;
            TriplePinchOn = s.TriplePinchOn;
            PinchHoldOn = s.PinchHoldOn;
            DoublePinchCallOn = s.DoublePinchCallOn;
            PinchHoldCallOn = s.PinchHoldCallOn;
            HoldActionLeft = s.HoldActionL;
            HoldActionRight = s.HoldActionR;
            DoubleTapEdgeVolume = s.DoubleTapEdgeVolume;
            CallPathControl = s.CallPathControl;
            Sidetone = s.Sidetone;
            NoiseControlsWithOneEarbud = s.NoiseControlsWithOneEarbud;
            Seamless = s.Seamless;
        });
        ApplyBattery(s);

        // The firmware forgets custom EQ gains when powered off; push them again once per connection.
        if (EqPreset == CustomEqPreset && !_customEqPushed)
        {
            _customEqPushed = true;
            PushCustomEq();
        }
    }

    private void ApplyDiagnostics(DeviceDiagnostics d)
    {
        Firmware = d.Firmware.Length > 0 ? d.Firmware : "—";
        Hardware = d.Hardware.Length > 0 ? d.Hardware : "—";
        Temperatures = d.TemperatureL is null && d.TemperatureR is null
            ? "—"
            : $"L {d.TemperatureL:0.0} °C  ·  R {d.TemperatureR:0.0} °C";
        Voltages = d.VoltageL is null && d.VoltageR is null
            ? "—"
            : $"L {d.VoltageL:0.00} V  ·  R {d.VoltageR:0.00} V";
    }

    // ───── Battery ─────

    private int _batteryLeft = -1, _batteryRight = -1, _batteryCase = -1;
    private string _stateLeft = "", _stateRight = "", _stateCase = "";
    private bool _chargingLeft, _chargingRight, _chargingCase;
    private Placement _placementL, _placementR;

    public int BatteryLeft { get => _batteryLeft; private set => Set(ref _batteryLeft, value); }
    public int BatteryRight { get => _batteryRight; private set => Set(ref _batteryRight, value); }
    public int BatteryCase { get => _batteryCase; private set => Set(ref _batteryCase, value); }
    public string StateLeft { get => _stateLeft; private set => Set(ref _stateLeft, value); }
    public string StateRight { get => _stateRight; private set => Set(ref _stateRight, value); }
    public string StateCase { get => _stateCase; private set => Set(ref _stateCase, value); }
    public bool ChargingLeft { get => _chargingLeft; private set => Set(ref _chargingLeft, value); }
    public bool ChargingRight { get => _chargingRight; private set => Set(ref _chargingRight, value); }
    public bool ChargingCase { get => _chargingCase; private set => Set(ref _chargingCase, value); }

    private void ApplyBattery(BatteryStatus s)
    {
        BatteryLeft = s.PlacementL == Placement.Disconnected ? -1 : Math.Clamp(s.Left, 0, 100);
        BatteryRight = s.PlacementR == Placement.Disconnected ? -1 : Math.Clamp(s.Right, 0, 100);
        BatteryCase = s.Case is > 0 and <= 100 ? s.Case : -1;
        ChargingLeft = s.ChargingL;
        ChargingRight = s.ChargingR;
        ChargingCase = s.ChargingCase;
        _placementL = s.PlacementL;
        _placementR = s.PlacementR;
        StateLeft = Describe(s.PlacementL, s.ChargingL);
        StateRight = Describe(s.PlacementR, s.ChargingR);
        StateCase = BatteryCase < 0 ? "Updates when a bud is in the case" : s.ChargingCase ? "Charging" : "";
        CheckLowBattery();
        RaiseTray();
    }

    private static string Describe(Placement placement, bool charging)
    {
        string where = placement switch
        {
            Placement.Wearing => "In ear",
            Placement.Idle => "Out of ear",
            Placement.InCase => "In case",
            Placement.ClosedCase => "In closed case",
            _ => "Not connected"
        };
        return charging ? $"{where} · charging" : where;
    }

    private void CheckLowBattery()
    {
        var levels = new List<int>();
        if (BatteryLeft > 0 && !ChargingLeft) levels.Add(BatteryLeft);
        if (BatteryRight > 0 && !ChargingRight) levels.Add(BatteryRight);
        if (levels.Count == 0)
            return;
        int lowest = levels.Min();
        if (lowest <= 15 && !_lowAlerted && _settings.LowBatteryAlert)
        {
            _lowAlerted = true;
            Notify?.Invoke("Buds battery low", $"{Selected?.Name}: {lowest}% left. Put them in the case to charge.");
        }
        else if (lowest >= 25)
        {
            _lowAlerted = false;
        }
    }

    private void ClearDeviceState()
    {
        BatteryLeft = BatteryRight = BatteryCase = -1;
        StateLeft = StateRight = StateCase = "";
        ChargingLeft = ChargingRight = ChargingCase = false;
        Firmware = Hardware = Temperatures = Voltages = "—";
        _isRinging = false;
        Raise(nameof(IsRinging));
        Raise(nameof(RingLabel));
        RaiseTray();
    }

    public string TrayText
    {
        get
        {
            if (Selected is null)
                return "Silverbud";
            string Level(int v) => v < 0 ? "—" : $"{v}%";
            string text = IsLinked
                ? $"{Selected.Name}\nL {Level(BatteryLeft)} · R {Level(BatteryRight)} · Case {Level(BatteryCase)}"
                : $"{Selected.Name}\n{PillText}";
            return text.Length > 120 ? text[..120] : text;
        }
    }

    private void RaiseTray()
    {
        Raise(nameof(TrayText));
        TrayTextChanged?.Invoke();
    }

    // ───── Sound ─────

    private int _noiseControl;
    private int _ambientVolume;
    private bool _ambientCustomOn;
    private int _ambientCustomLeft, _ambientCustomRight, _ambientTone;
    private int _eqPreset;
    private int _balance = 16;
    private bool _gameMode;

    /// <summary>0 off · 1 noise canceling · 2 ambient sound</summary>
    public int NoiseControl
    {
        get => _noiseControl;
        set
        {
            if (Set(ref _noiseControl, value) && !_syncing)
                Send(MsgId.NoiseControls, (byte)value);
        }
    }

    public int AmbientVolume
    {
        get => _ambientVolume;
        set
        {
            if (Set(ref _ambientVolume, value) && !_syncing)
                Debounce("ambient", () => Send(MsgId.AmbientVolume, (byte)AmbientVolume));
        }
    }

    public bool AmbientCustomOn { get => _ambientCustomOn; set => SetAmbientCustom(ref _ambientCustomOn, value); }
    public int AmbientCustomLeft { get => _ambientCustomLeft; set => SetAmbientCustom(ref _ambientCustomLeft, value); }
    public int AmbientCustomRight { get => _ambientCustomRight; set => SetAmbientCustom(ref _ambientCustomRight, value); }
    public int AmbientTone { get => _ambientTone; set => SetAmbientCustom(ref _ambientTone, value); }

    private void SetAmbientCustom<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name) || _syncing)
            return;
        Debounce("ambient-custom", () => Send(MsgId.CustomizeAmbientSound,
            B(AmbientCustomOn), (byte)AmbientCustomLeft, (byte)AmbientCustomRight, (byte)AmbientTone));
    }

    /// <summary>0 normal · 1 bass boost · 2 soft · 3 dynamic · 4 clear · 5 treble boost · 6 custom</summary>
    public int EqPreset
    {
        get => _eqPreset;
        set
        {
            if (!Set(ref _eqPreset, value))
                return;
            Raise(nameof(IsCustomEq));
            if (_syncing)
                return;
            if (value == CustomEqPreset)
                PushCustomEq();
            else
                Send(MsgId.Equalizer, (byte)value);
        }
    }

    public bool IsCustomEq => EqPreset == CustomEqPreset;

    private void OnBandChanged()
    {
        if (_demo)
            return;
        _settings.CustomEq = EqBands.Select(b => b.Gain).ToArray();
        Debounce("eq-save", _settings.Save, 800);
        if (!_syncing && IsCustomEq)
            Debounce("eq", PushCustomEq);
    }

    private void PushCustomEq()
    {
        var payload = new byte[10];
        payload[0] = 9;
        for (int i = 0; i < 9; i++)
            payload[i + 1] = unchecked((byte)(sbyte)EqBands[i].Gain);
        Send(MsgId.CustomEqualizerSend, payload);
        Send(MsgId.Equalizer, CustomEqPreset + 1);
    }

    public int Balance
    {
        get => _balance;
        set
        {
            if (!Set(ref _balance, value))
                return;
            Raise(nameof(BalanceText));
            if (!_syncing)
                Debounce("balance", () => Send(MsgId.SetHearingEnhancements, (byte)Balance));
        }
    }

    public string BalanceText => Balance == 16 ? "Centered" : Balance < 16 ? $"Left +{16 - Balance}" : $"Right +{Balance - 16}";

    public bool GameMode
    {
        get => _gameMode;
        set
        {
            if (Set(ref _gameMode, value) && !_syncing)
                Send(MsgId.AdjustSoundSync, B(value));
        }
    }

    // ───── Controls ─────

    private bool _touchEnabled = true, _pinchOn = true, _doublePinchOn = true, _triplePinchOn = true, _pinchHoldOn = true;
    private bool _doublePinchCallOn = true, _pinchHoldCallOn = true;
    private int _holdActionLeft = 2, _holdActionRight = 2;
    private bool _doubleTapEdgeVolume;

    public bool TouchEnabled { get => _touchEnabled; set => SetTouch(ref _touchEnabled, value); }
    public bool PinchOn { get => _pinchOn; set => SetTouch(ref _pinchOn, value); }
    public bool DoublePinchOn { get => _doublePinchOn; set => SetTouch(ref _doublePinchOn, value); }
    public bool TriplePinchOn { get => _triplePinchOn; set => SetTouch(ref _triplePinchOn, value); }
    public bool PinchHoldOn { get => _pinchHoldOn; set => SetTouch(ref _pinchHoldOn, value); }
    public bool DoublePinchCallOn { get => _doublePinchCallOn; set => SetTouch(ref _doublePinchCallOn, value); }
    public bool PinchHoldCallOn { get => _pinchHoldCallOn; set => SetTouch(ref _pinchHoldCallOn, value); }

    private void SetTouch(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name) || _syncing)
            return;
        Send(MsgId.LockTouchpad, B(TouchEnabled), B(PinchOn), B(DoublePinchOn), B(TriplePinchOn), B(PinchHoldOn),
            B(DoublePinchCallOn), B(PinchHoldCallOn));
    }

    /// <summary>1 voice assistant · 2 noise controls · 3 volume · 4 Spotify</summary>
    public int HoldActionLeft { get => _holdActionLeft; set => SetHoldAction(ref _holdActionLeft, value); }
    public int HoldActionRight { get => _holdActionRight; set => SetHoldAction(ref _holdActionRight, value); }

    private void SetHoldAction(ref int field, int value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Set(ref field, value, name) && !_syncing)
            Send(MsgId.SetTouchpadOption, (byte)HoldActionLeft, (byte)HoldActionRight);
    }

    public bool DoubleTapEdgeVolume
    {
        get => _doubleTapEdgeVolume;
        set
        {
            if (Set(ref _doubleTapEdgeVolume, value) && !_syncing)
                Send(MsgId.OutsideDoubleTap, B(value));
        }
    }

    // ───── Find ─────

    private bool _isRinging, _muteLeft, _muteRight;

    public bool IsRinging
    {
        get => _isRinging;
        set
        {
            if (!Set(ref _isRinging, value))
                return;
            Raise(nameof(RingLabel));
            if (!_syncing)
                Send(value ? MsgId.FindMyEarbudsOnWearingStart : MsgId.FindMyEarbudsStop);
        }
    }

    public string RingLabel => IsRinging ? "Stop ringing" : "Ring buds";

    public bool MuteLeft { get => _muteLeft; set => SetMute(ref _muteLeft, value); }
    public bool MuteRight { get => _muteRight; set => SetMute(ref _muteRight, value); }

    private void SetMute(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Set(ref field, value, name) && !_syncing)
            Send(MsgId.MuteEarbud, B(MuteLeft), B(MuteRight));
    }

    private void ToggleRing() => IsRinging = !IsRinging;

    // ───── Calls & connection ─────

    private bool _callPathControl, _sidetone, _noiseControlsWithOneEarbud, _seamless;

    public bool CallPathControl
    {
        get => _callPathControl;
        set { if (Set(ref _callPathControl, value) && !_syncing) Send(MsgId.SetCallPathControl, B(!value)); }
    }

    public bool Sidetone
    {
        get => _sidetone;
        set { if (Set(ref _sidetone, value) && !_syncing) Send(MsgId.SetSidetone, B(value)); }
    }

    public bool NoiseControlsWithOneEarbud
    {
        get => _noiseControlsWithOneEarbud;
        set { if (Set(ref _noiseControlsWithOneEarbud, value) && !_syncing) Send(MsgId.SetAncWithOneEarbud, B(value)); }
    }

    public bool Seamless
    {
        get => _seamless;
        set { if (Set(ref _seamless, value) && !_syncing) Send(MsgId.SetSeamlessConnection, B(!value)); }
    }

    private void EnterPairingMode()
    {
        if (Confirm?.Invoke("Turn on pairing mode?",
                "The buds will disconnect from this PC so a new phone or computer can find them.") != true)
            return;
        _userDisconnected = true;
        Send(MsgId.PairingMode);
        StatusText = "Pairing mode is on. Pair the buds from the other device now.";
    }

    private void FactoryReset()
    {
        if (Confirm?.Invoke("Reset the buds to factory settings?",
                "Every setting returns to default and the buds forget all paired devices, including this PC. This can't be undone.") != true)
            return;
        _userDisconnected = true;
        Send(MsgId.Reset);
        StatusText = "Resetting. Pair the buds again when they restart.";
    }

    // ───── About ─────

    private string _firmware = "—", _hardware = "—", _temperatures = "—", _voltages = "—";
    public string Firmware { get => _firmware; private set => Set(ref _firmware, value); }
    public string Hardware { get => _hardware; private set => Set(ref _hardware, value); }
    public string Temperatures { get => _temperatures; private set => Set(ref _temperatures, value); }
    public string Voltages { get => _voltages; private set => Set(ref _voltages, value); }

    // ───── App preferences ─────

    public bool AutoConnect
    {
        get => _settings.AutoConnect;
        set { _settings.AutoConnect = value; _settings.Save(); Raise(); }
    }

    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set { _settings.CloseToTray = value; _settings.Save(); Raise(); }
    }

    public bool LowBatteryAlert
    {
        get => _settings.LowBatteryAlert;
        set { _settings.LowBatteryAlert = value; _settings.Save(); Raise(); }
    }

    public bool StartWithWindows
    {
        get
        {
            try { return Autostart.IsEnabled; }
            catch { return false; }
        }
        set
        {
            try { Autostart.IsEnabled = value; }
            catch (Exception ex) { Log.Write($"Autostart change failed: {ex.Message}"); }
            Raise();
        }
    }

    // ───── Helpers ─────

    private static byte B(bool value) => value ? (byte)1 : (byte)0;

    private static void Shell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't open {target}: {ex.Message}");
        }
    }

    /// <summary>Fills the window with sample data so the layout can be reviewed without buds (--demo).</summary>
    public void LoadDemo()
    {
        _demo = true;
        int[] curve = [4, 3, 1, 0, -1, 0, 2, 3, 2];
        for (int i = 0; i < 9; i++)
            EqBands[i].Gain = curve[i];
        var buds = new DeviceItem("Galaxy Buds3 (7C2E)", "B4:9D:02:7C:2E:41", paired: true);
        PairedDevices.Add(buds);
        NearbyDevices.Add(new DeviceItem("Galaxy Buds3 Pro (19AF)", "58:A6:39:19:AF:02", paired: false));
        ListReady = true;
        _selected = buds;
        Raise(nameof(Selected));
        Raise(nameof(HasSelection));
        LinkState = LinkState.Connected;
        ApplyExtended(new ExtendedStatus
        {
            Left = 82, Right = 64, Case = 47,
            PlacementL = Placement.Wearing, PlacementR = Placement.Wearing,
            NoiseControl = 1, AmbientVolume = 1, EqMode = 7, Balance = 16, TouchEnabled = true,
            PinchOn = true, DoublePinchOn = true, TriplePinchOn = true, PinchHoldOn = true,
            DoublePinchCallOn = true, PinchHoldCallOn = true, HoldActionL = 2, HoldActionR = 3,
            CallPathControl = true, Seamless = true, NoiseControlsWithOneEarbud = true
        });
        ApplyDiagnostics(new DeviceDiagnostics
        {
            Firmware = "R530XXU0AXH8", Hardware = "rev0.4",
            TemperatureL = 31.4, TemperatureR = 31.9, VoltageL = 3.98, VoltageR = 3.91
        });
    }

    public void Dispose()
    {
        _scanTimer?.Cancel();
        _scanner.Dispose();
        var link = _link;
        _link = null;
        link?.DisposeAsync().AsTask().Wait(1500);
    }
}
