using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace AppleMusicLyrics.Core.Tests;

public sealed class InstanceActivationTests
{
    private static string NewPipeName() => "AppleMusicDesktopLyrics-Test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void SameDirectoryUsesStableCaseInsensitiveUnicodeName()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppleMusic 歌词 가사");
        var first = InstanceActivationServer.BuildName(directory, 1);
        var equivalent = InstanceActivationServer.BuildName(directory.ToUpperInvariant() + Path.DirectorySeparatorChar + ".", 1);

        Assert.Equal(first, equivalent);
        Assert.Matches("^AppleMusicDesktopLyrics-1-[0-9A-F]{24}$", first);
        Assert.DoesNotContain("歌词", first);
    }

    [Fact]
    public void DifferentDirectoriesAndLogonSessionsAreIndependent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppleMusicLyrics");
        var first = InstanceActivationServer.BuildName(directory, 1);

        Assert.NotEqual(first, InstanceActivationServer.BuildName(directory + "-portable", 1));
        Assert.NotEqual(first, InstanceActivationServer.BuildName(directory, 2));
    }

    [Fact]
    public async Task RepeatedLaunchesActivateTheExistingInstanceAndStartIsIdempotent()
    {
        var name = NewPipeName();
        var activations = 0;
        using var server = new InstanceActivationServer(name, () => Interlocked.Increment(ref activations));
        server.Start();
        server.Start();

        Assert.True(await InstanceActivationServer.SendSettingsAsync(name, TimeSpan.FromSeconds(3)));
        Assert.True(await InstanceActivationServer.SendSettingsAsync(name, TimeSpan.FromSeconds(3)));
        Assert.Equal(2, Volatile.Read(ref activations));
    }

    [Fact]
    public async Task ConcurrentLaunchesReceiveBoundedAcknowledgements()
    {
        var name = NewPipeName();
        var activations = 0;
        using var server = new InstanceActivationServer(name, () => Interlocked.Increment(ref activations));
        server.Start();

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => InstanceActivationServer.SendSettingsAsync(name, TimeSpan.FromSeconds(5))));

        Assert.All(results, result => Assert.True(result));
        Assert.Equal(6, Volatile.Read(ref activations));
    }

    [Fact]
    public async Task MissingInstanceTimesOutInsteadOfHanging()
    {
        var watch = Stopwatch.StartNew();
        Assert.False(await InstanceActivationServer.SendSettingsAsync(NewPipeName(), TimeSpan.FromMilliseconds(100)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CancelledOrZeroDurationRequestDoesNotConnect()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.False(await InstanceActivationServer.SendSettingsAsync(NewPipeName(), TimeSpan.FromSeconds(5), cancellation.Token));
        Assert.False(await InstanceActivationServer.SendSettingsAsync(NewPipeName(), TimeSpan.Zero));
    }

    [Theory]
    [InlineData("EXIT\n")]
    [InlineData("SETTINGS with arguments\n")]
    [InlineData("XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX\n")]
    public async Task UnsupportedAndOverlongCommandsCannotActivateSettings(string command)
    {
        var name = NewPipeName();
        var activations = 0;
        using var server = new InstanceActivationServer(name, () => Interlocked.Increment(ref activations));
        server.Start();
        using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(deadline.Token);
            await client.WriteAsync(Encoding.ASCII.GetBytes(command), deadline.Token);
            var reply = new byte[3];
            Assert.Equal(0, await client.ReadAsync(reply, deadline.Token));
        }
        Assert.Equal(0, Volatile.Read(ref activations));
        Assert.True(await InstanceActivationServer.SendSettingsAsync(name, TimeSpan.FromSeconds(3)));
        Assert.Equal(1, Volatile.Read(ref activations));
    }

    [Fact]
    public async Task IncompleteClientCannotBlockFutureLaunchesForever()
    {
        var name = NewPipeName();
        var activations = 0;
        using var server = new InstanceActivationServer(name, () => Interlocked.Increment(ref activations));
        server.Start();
        using var stalledClient = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await stalledClient.ConnectAsync(deadline.Token);
        await stalledClient.WriteAsync("SET"u8.ToArray(), deadline.Token);

        Assert.True(await InstanceActivationServer.SendSettingsAsync(name, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref activations));
    }

    [Fact]
    public async Task DisposeCancelsConnectedReadWithoutWaitingOnTheCaller()
    {
        var name = NewPipeName();
        var server = new InstanceActivationServer(name, () => throw new InvalidOperationException("Must not activate."));
        server.Start();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await client.ConnectAsync(deadline.Token);
        var watch = Stopwatch.StartNew();

        server.Dispose();
        server.Dispose();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Throws<ObjectDisposedException>(() => server.Start());
        Assert.False(await InstanceActivationServer.SendSettingsAsync(name, TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public async Task CallbackFailureDoesNotAcknowledgeOrKillTheServer()
    {
        var name = NewPipeName();
        var requests = 0;
        using var server = new InstanceActivationServer(name, () =>
        {
            if (Interlocked.Increment(ref requests) == 1) throw new InvalidOperationException("UI closed during dispatch.");
        });
        server.Start();

        Assert.False(await InstanceActivationServer.SendSettingsAsync(name, TimeSpan.FromSeconds(3)));
        Assert.True(await InstanceActivationServer.SendSettingsAsync(name, TimeSpan.FromSeconds(3)));
        Assert.Equal(2, Volatile.Read(ref requests));
    }
}
