using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Ziwa.Application.Abstractions;
using Ziwa.Domain.Platform;

namespace Ziwa.Platform.Windows.SingleInstance;

[SupportedOSPlatform("windows")]
public sealed class WindowsSingleInstanceCoordinator : ISingleInstanceCoordinator
{
    private const string DefaultMutexName = @"Local\ZiwaStudio_SingleInstance_v1";
    private const string DefaultPipeName = "ZiwaStudio_ActivationPipe_v1";

    private readonly string _mutexName;
    private readonly string _pipeName;
    private Mutex? _ownedMutex;
    private CancellationTokenSource? _listenCts;
    private Task? _listenTask;
    private bool _disposed;

    public WindowsSingleInstanceCoordinator(string? mutexName = null, string? pipeName = null)
    {
        _mutexName = string.IsNullOrWhiteSpace(mutexName) ? DefaultMutexName : mutexName.Trim();
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName.Trim();
    }

    public event Action<string?>? SecondaryInstanceActivated;

    public bool TryAcquireOrForward(string[] args, out bool forwardedToPrimary)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var mutex = new Mutex(initiallyOwned: true, _mutexName, out bool createdNew);
        if (createdNew)
        {
            _ownedMutex = mutex;
            forwardedToPrimary = false;
            return true;
        }

        mutex.Dispose();
        string? projectPath = StartupArgumentParser.TryExtractZiwaProjectPath(args);
        forwardedToPrimary = TryForwardActivationToPrimary(projectPath);
        return false;
    }

    public void StartListening()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listenCts is not null)
        {
            return;
        }

        _listenCts = new CancellationTokenSource();
        CancellationToken ct = _listenCts.Token;
        var initialServer = CreatePipeServer();
        _listenTask = Task.Run(() => ListenLoopAsync(initialServer, ct), CancellationToken.None);
    }

    private NamedPipeServerStream CreatePipeServer() =>
        new(
            _pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

    private bool TryForwardActivationToPrimary(string? projectPath)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.None);
            client.Connect(timeout: 2000);

            string payload = JsonSerializer.Serialize(new ActivationEnvelope(projectPath)) + "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ListenLoopAsync(NamedPipeServerStream? pendingServer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = pendingServer;
            pendingServer = null;
            try
            {
                server ??= CreatePipeServer();
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    try
                    {
                        ActivationEnvelope? env = JsonSerializer.Deserialize<ActivationEnvelope>(line);
                        SecondaryInstanceActivated?.Invoke(env?.OpenPath);
                    }
                    catch (JsonException)
                    {
                        SecondaryInstanceActivated?.Invoke(null);
                    }
                }
                else
                {
                    SecondaryInstanceActivated?.Invoke(null);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_listenCts is not null)
        {
            try
            {
                _listenCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _listenCts.Dispose();
                _listenCts = null;
            }
        }

        if (_ownedMutex is not null)
        {
            try
            {
                _ownedMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
            finally
            {
                _ownedMutex.Dispose();
                _ownedMutex = null;
            }
        }
    }

    private sealed record ActivationEnvelope(string? OpenPath);
}
