using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;

namespace Silverbud.Core;

/// <summary>
/// The control channel to the buds: an RFCOMM socket on Samsung's earbud service.
/// Audio itself runs over the normal Windows Bluetooth audio profiles; this link only carries settings.
/// </summary>
public sealed class BudsLink : IAsyncDisposable
{
    public static readonly Guid ServiceUuid = new("2e73a4ad-332d-41fc-90e2-16bef06523f2");

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly FrameReader _reader = new();
    private readonly CancellationTokenSource _cts = new();
    private BluetoothDevice? _device;
    private StreamSocket? _socket;
    private Stream? _output;
    private int _closed;

    public event Action<SppFrame>? FrameReceived;
    public event Action<string?>? Disconnected;

    public BudsLink()
    {
        _reader.Discarded += bytes => Log.Write($"Unparsed bytes ({bytes.Length}): {Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 96)))}");
    }

    public async Task ConnectAsync(string deviceId)
    {
        _device = await BluetoothDevice.FromIdAsync(deviceId)
                  ?? throw new BudsException("Windows couldn't open these buds. Remove them and pair them again.");

        RfcommDeviceService? service = null;
        foreach (var mode in new[] { BluetoothCacheMode.Uncached, BluetoothCacheMode.Cached })
        {
            try
            {
                var result = await WithTimeout(
                    _device.GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(ServiceUuid), mode).AsTask(), 12000);
                Log.Write($"Service lookup ({mode}): {result.Error}, {result.Services.Count} found");
                if (result.Services.Count > 0)
                {
                    service = result.Services[0];
                    break;
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Service lookup ({mode}) failed: {ex.Message}");
            }
        }
        if (service is null)
            throw new BudsException("The buds didn't answer. Open the case or put them in your ears, keep them close to the PC, and try again.");

        _socket = new StreamSocket();
        await WithTimeout(_socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName).AsTask(), 15000);
        _output = _socket.OutputStream.AsStreamForWrite(0);
        var input = _socket.InputStream.AsStreamForRead(0);
        _ = Task.Run(() => ReadLoop(input));
    }

    public async Task SendAsync(SppFrame frame)
    {
        var output = _output ?? throw new InvalidOperationException("Not connected");
        var bytes = frame.Encode();
        await _writeLock.WaitAsync();
        try
        {
            await output.WriteAsync(bytes);
            await output.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoop(Stream input)
    {
        var buffer = new byte[2048];
        string? reason = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int read = await input.ReadAsync(buffer, _cts.Token);
                if (read <= 0)
                {
                    reason = "The buds closed the connection.";
                    break;
                }
                foreach (var frame in _reader.Push(buffer.AsSpan(0, read)))
                    FrameReceived?.Invoke(frame);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            Log.Write($"Read loop ended: {ex.Message}");
        }
        await CloseAsync(reason);
    }

    private async Task CloseAsync(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
            return;
        _cts.Cancel();
        try { _socket?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        _socket = null;
        _output = null;
        await Task.Yield();
        Disconnected?.Invoke(reason);
    }

    public ValueTask DisposeAsync() => new(CloseAsync(null));

    private static async Task<T> WithTimeout<T>(Task<T> task, int ms)
    {
        if (await Task.WhenAny(task, Task.Delay(ms)) != task)
            throw new TimeoutException();
        return await task;
    }

    private static async Task WithTimeout(Task task, int ms)
    {
        if (await Task.WhenAny(task, Task.Delay(ms)) != task)
            throw new TimeoutException();
        await task;
    }

    /// <summary>Turns socket errors into something a person can act on.</summary>
    public static string Explain(Exception? ex) => ex switch
    {
        null => "Couldn't connect to the buds.",
        BudsException b => b.Message,
        TimeoutException => "The buds didn't answer in time. Take them out of the case, keep them close to the PC, and try again.",
        _ when (uint)ex.HResult == 0x80070490 =>
            "The buds' control channel isn't available. Put both buds in the case, close the lid for 10 seconds, then try again.",
        _ when (uint)ex.HResult == 0x80072740 =>
            "Another app is already using the buds' control channel (for example Galaxy Buds Manager). Close it and try again.",
        _ when (uint)ex.HResult == 0x8007274C || (uint)ex.HResult == 0x8007271D =>
            "The buds didn't answer. Open the case or put them in your ears, keep them close to the PC, and try again.",
        _ => $"Couldn't connect to the buds: {ex.Message}"
    };
}

public sealed class BudsException(string message) : Exception(message);
