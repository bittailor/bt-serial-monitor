using System.IO.Ports;
using System.Text;

var cts = new CancellationTokenSource();
var monitoring = false;
Console.CancelKeyPress += (_, e) =>
{
    // Before monitoring starts, let Ctrl+C terminate the process immediately (ReadLine would block otherwise).
    e.Cancel = monitoring;
    cts.Cancel();
};

var portName = SelectPort(cts.Token);
if (portName is null)
    return;

var baudRate = SelectBaudRate();
if (cts.IsCancellationRequested)
    return;

Console.WriteLine($"Monitoring {portName} @ {baudRate} baud. Type and press Enter to send. Ctrl+C to exit.");

monitoring = true;
var portLock = new object();
SerialPort? currentPort = null;

var inputThread = new Thread(() =>
{
    while (!cts.IsCancellationRequested)
    {
        string? line;
        try { line = Console.ReadLine(); }
        catch { break; }
        if (line is null)
            break;

        lock (portLock)
        {
            if (currentPort is { IsOpen: true })
            {
                try { currentPort.Write(line + "\r\n"); }
                catch (Exception ex) { WriteStatus($"Send failed: {ex.Message}"); }
            }
            else
            {
                WriteStatus("Not connected - input discarded.");
            }
        }
    }
}) { IsBackground = true };
inputThread.Start();

try
{
    await RunAsync(cts.Token);
}
catch (OperationCanceledException) { }
finally
{
    lock (portLock)
    {
        currentPort?.Dispose();
        currentPort = null;
    }
    WriteStatus("Exiting.");
}

async Task RunAsync(CancellationToken token)
{
    var decoder = Encoding.UTF8.GetDecoder();
    var buffer = new byte[4096];
    var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

    while (!token.IsCancellationRequested)
    {
        var port = await ConnectAsync(token);
        lock (portLock) currentPort = port;
        WriteStatus($"Connected to {portName}.");

        try
        {
            using var reg = token.Register(() => { try { port.Close(); } catch { } });
            while (!token.IsCancellationRequested)
            {
                int read = await port.BaseStream.ReadAsync(buffer, token);
                if (read == 0)
                    throw new IOException("Port closed.");
                int count = decoder.GetChars(buffer, 0, read, chars, 0);
                Console.Write(chars, 0, count);
            }
        }
        // On Windows a vanishing port aborts the pending overlapped read, surfacing as OperationCanceledException.
        catch (Exception ex) when (!token.IsCancellationRequested && ex is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            WriteStatus($"Disconnected from {portName}: {ex.Message}");
        }
        catch when (token.IsCancellationRequested) { }
        finally
        {
            lock (portLock) currentPort = null;
            try { port.Dispose(); } catch { }
            decoder.Reset();
        }
    }
}

async Task<SerialPort> ConnectAsync(CancellationToken token)
{
    bool announced = false;
    while (true)
    {
        token.ThrowIfCancellationRequested();
        if (SerialPort.GetPortNames().Contains(portName, StringComparer.OrdinalIgnoreCase))
        {
            var port = new SerialPort(portName, baudRate) { DtrEnable = true, RtsEnable = true };
            try
            {
                port.Open();
                return port;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                port.Dispose();
                if (!announced)
                    WriteStatus($"Cannot open {portName}: {ex.Message}. Retrying...");
                announced = true;
            }
        }
        else if (!announced)
        {
            WriteStatus($"Waiting for {portName}...");
            announced = true;
        }
        await Task.Delay(1000, token);
    }
}

static string? SelectPort(CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        var ports = SerialPort.GetPortNames().Distinct().OrderBy(p => p).ToArray();
        if (ports.Length == 0)
        {
            Console.WriteLine("No serial ports found. Press Enter to rescan.");
            if (Console.ReadLine() is null) return null;
            continue;
        }

        Console.WriteLine("Available ports:");
        for (int i = 0; i < ports.Length; i++)
            Console.WriteLine($"  [{i + 1}] {ports[i]}");
        Console.Write("Select port (Enter = rescan): ");
        var input = Console.ReadLine();
        if (input is null || token.IsCancellationRequested) return null;
        if (int.TryParse(input, out int idx) && idx >= 1 && idx <= ports.Length)
            return ports[idx - 1];
        if (input.Length > 0)
            Console.WriteLine("Invalid selection.");
    }
    return null;
}

static int SelectBaudRate()
{
    int[] rates = [300, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];
    const int defaultRate = 115200;

    Console.WriteLine("Baud rates:");
    for (int i = 0; i < rates.Length; i++)
        Console.WriteLine($"  [{i + 1}] {rates[i]}{(rates[i] == defaultRate ? " (default)" : "")}");

    while (true)
    {
        Console.Write($"Select baud rate (Enter = {defaultRate}): ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input)) return defaultRate;
        if (int.TryParse(input, out int idx) && idx >= 1 && idx <= rates.Length)
            return rates[idx - 1];
        Console.WriteLine("Invalid selection.");
    }
}

static void WriteStatus(string message)
{
    var color = Console.ForegroundColor;
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine();
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    Console.ForegroundColor = color;
}
