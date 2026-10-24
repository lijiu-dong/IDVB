using System.IO.Pipes;
using System.Text;
using System.Security.Cryptography;

namespace IDVBuff.Lifecycle;

internal sealed class GuiInstanceCoordinator : IDisposable
{
    private const string StandardMutexName = "Local\\IdentityVisionBridge.Gui";
    private const string DevMutexName = "Local\\IdentityVisionBridge.Gui.Dev.v2";
    private const string StandardActivationPipeName = "IdentityVisionBridge.GuiActivation.v1";
    private const string DevActivationPipeName = "IdentityVisionBridge.GuiActivation.Dev.v2";

    private readonly string _primaryMutexName;
    private readonly string _activationPipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private Mutex? _mutex;
    private Task? _listener;

    public static event EventHandler? ActivationRequested;

    public GuiInstanceCoordinator(bool isDevelopmentInstance = false)
    {
        _primaryMutexName = isDevelopmentInstance ? DevMutexName : StandardMutexName;
        // The one development mutex rejects concurrent builds, while activation
        // can only reach this exact deployment and compiled assembly.
        var buildIdentity = Path.GetFullPath(AppContext.BaseDirectory).ToUpperInvariant()
            + typeof(GuiInstanceCoordinator).Assembly.ManifestModule.ModuleVersionId;
        var buildKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(buildIdentity)));
        _activationPipeName = isDevelopmentInstance
            ? $"{DevActivationPipeName}.{buildKey}" : StandardActivationPipeName;
    }

    public bool TryAcquirePrimary()
    {
        try
        {
            _mutex = new Mutex(true, _primaryMutexName, out var ownsPrimary);
            if (!ownsPrimary)
                return false;

            return true;
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void StartListening()
    {
        _listener ??= Task.Run(() => ListenAsync(_shutdown.Token));
    }

    public bool NotifyPrimaryInstance()
    {
        // An installed application must never consume a development activation.
        return TryNotifyPipe(_activationPipeName);
    }

    private static bool TryNotifyPipe(string pipeName)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.Connect(500);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine("activate");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RaiseActivationRequested() =>
        ActivationRequested?.Invoke(null, EventArgs.Empty);

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                _activationPipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, true);
                if (string.Equals(
                        await reader.ReadLineAsync(cancellationToken),
                        "activate",
                        StringComparison.Ordinal))
                    RaiseActivationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
            }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _listener?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _shutdown.Dispose();
        _mutex?.Dispose();
    }
}
