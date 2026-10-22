using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace SolutionDeployer.App;

/// <summary>
/// Keeps one copy of the app running per user and install folder. The app lives on in the tray after its window
/// closes, so launching it again must bring that copy's window back rather than start a second one (which would
/// also fight over the MCP port). A dev build and an installed build use different folders, so they don't clash.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    /// <summary>Raised (on a background thread) when another launch asks this copy to show its window.</summary>
    public event Action? ActivationRequested;

    /// <summary>Claims the instance, or returns null when another copy already runs.</summary>
    public static SingleInstance? TryAcquire()
    {
        var name = InstanceName();
        var mutex = new Mutex(initiallyOwned: true, $"{name}.lock", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstance(mutex, $"{name}.activate");
    }

    /// <summary>Asks the running copy to show its window. Best-effort.</summary>
    public static void ActivateExisting()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", $"{InstanceName()}.activate", PipeDirection.Out);
            client.Connect(TimeSpan.FromSeconds(2));
            client.WriteByte(1);
        }
        catch
        {
            // The other copy is starting up or shutting down; nothing more to do.
        }
    }

    /// <summary>Starts listening for <see cref="ActivateExisting"/> from later launches.</summary>
    public void Listen() => _ = Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                ActivationRequested?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                await Task.Delay(500); // e.g. a broken connection; keep serving
            }
        }
    });

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released on another thread already, or never owned.
        }

        _mutex.Dispose();
    }

    private static string InstanceName()
    {
        var folder = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
            folder = folder.ToUpperInvariant();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Environment.UserName}|{folder}")))[..16];
        return $"3ai.SolutionDeployer.{hash}";
    }
}
