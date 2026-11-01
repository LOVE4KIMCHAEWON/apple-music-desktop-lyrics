using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace AppleMusicLyrics.Core;

/// <summary>
/// Sends an explicit request to show the settings of an existing local instance.
/// The callback must enqueue UI work and return promptly; this class never waits for UI work.
/// </summary>
public sealed class InstanceActivationServer : IDisposable
{
    private const int MaximumCommandLength = 32;
    private static readonly byte[] SettingsCommand = "SETTINGS\n"u8.ToArray();
    private static readonly byte[] Acknowledgement = "OK\n"u8.ToArray();
    private readonly string _pipeName;
    private readonly Action _onSettingsRequested;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopToken;
    private NamedPipeServerStream? _activePipe;
    private Task? _worker;
    private bool _disposed;

    public InstanceActivationServer(string pipeName, Action onSettingsRequested)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(onSettingsRequested);
        _pipeName = pipeName;
        _onSettingsRequested = onSettingsRequested;
        _stopToken = _stop.Token;
    }

    /// <summary>Identifies one data directory in one Windows logon session.</summary>
    public static string BuildName(string dataDirectory, int sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentOutOfRangeException.ThrowIfNegative(sessionId);
        var canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath)))[..24];
        return $"AppleMusicDesktopLyrics-{sessionId}-{hash}";
    }

    /// <summary>Starts listening once. A second call has no effect.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _worker ??= Task.Run(ListenAsync);
        }
    }

    /// <summary>
    /// Returns true only after an existing instance accepts the fixed settings request.
    /// A missing, older, inaccessible, or shutting-down instance returns false within the timeout.
    /// </summary>
    public static async Task<bool> SendSettingsAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (timeout <= TimeSpan.Zero || cancellationToken.IsCancellationRequested) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            await pipe.WriteAsync(SettingsCommand, deadline.Token).ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            return await ReadBoundedLineAsync(pipe, deadline.Token).ConfigureAwait(false) == "OK";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task ListenAsync()
    {
        while (!_stopToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 64, 64);
                lock (_gate)
                {
                    if (_disposed) return;
                    _activePipe = pipe;
                }
                try
                {
                    await pipe.WaitForConnectionAsync(_stopToken).ConfigureAwait(false);
                    using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(_stopToken);
                    requestDeadline.CancelAfter(TimeSpan.FromSeconds(1));
                    if (await ReadBoundedLineAsync(pipe, requestDeadline.Token).ConfigureAwait(false) != "SETTINGS") continue;
                    try { _onSettingsRequested(); }
                    catch (Exception) { continue; } // A closing UI must not terminate the listener task.
                    await pipe.WriteAsync(Acknowledgement, requestDeadline.Token).ConfigureAwait(false);
                    await pipe.FlushAsync(requestDeadline.Token).ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_activePipe, pipe)) _activePipe = null;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException)
            {
                if (_stopToken.IsCancellationRequested) return;
                // Bound retries if a client disconnects or another process temporarily owns the name.
                try { await Task.Delay(25, _stopToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static async Task<string?> ReadBoundedLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var line = new StringBuilder(MaximumCommandLength);
        while (line.Length <= MaximumCommandLength)
        {
            if (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) == 0) return null;
            if (buffer[0] == (byte)'\n') return line.ToString();
            if (buffer[0] > 127 || line.Length == MaximumCommandLength) return null;
            line.Append((char)buffer[0]);
        }
        return null;
    }

    /// <summary>Cancels listening without waiting on the UI thread or a client.</summary>
    public void Dispose()
    {
        NamedPipeServerStream? pipe;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pipe = _activePipe;
            _activePipe = null;
        }
        _stop.Cancel();
        pipe?.Dispose();
        _stop.Dispose();
    }
}
