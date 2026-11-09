using System.Net;
using System.Net.Sockets;

namespace RunnerManager.Tests;

/// <summary>The joining PC accepts a code only after its own user has confirmed the 6-digit code.</summary>
public class JoinConfirmationTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);

    private static Pairing.PairingHost StartHost(byte[] key, Action<Pairing.JoinRequest> answer, out string address)
    {
        var port = TestPorts.FreeRange(2);
        var host = new Pairing.PairingHost(key, "HOSTPC", port, port + 1);
        host.JoinRequested += answer;
        host.Start();
        address = $"127.0.0.1:{port}";
        return host;
    }

    [Fact]
    public async Task TheCode_IsReturnedOnlyAfterThisPcConfirms()
    {
        var key = CommandSigner.NewKey();
        using var host = StartHost(key, request => request.Allow(), out var address);
        var sent = new TaskCompletionSource();
        host.Joined += _ => sent.TrySetResult();

        var asked = new TaskCompletionSource<Pairing.JoinOffer>();
        var codesMatch = new TaskCompletionSource<bool>();
        var join = Pairing.JoinAsync(address, "NEWPC", _ => { },
            offer => { asked.TrySetResult(offer); return codesMatch.Task; }, CancellationToken.None);

        // The host allowed at once and has sent its code, but nobody has confirmed on this PC.
        await sent.Task.WaitAsync(Step);
        var offer = await asked.Task.WaitAsync(Step);
        Assert.Equal(new Pairing.JoinOffer("HOSTPC", Managed: false), offer);
        await Task.Delay(200);
        Assert.False(join.IsCompleted);

        codesMatch.SetResult(true);
        Assert.Equal(key, (await join.WaitAsync(Step)).PairingKey);
    }

    [Fact]
    public async Task ACodeThatIsNotConfirmed_IsNotReturned()
    {
        // Any device on the network can answer a search under another PC's name and allow by itself.
        using var host = StartHost(CommandSigner.NewKey(), request => request.Allow(), out var address);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Pairing.JoinAsync(address, "NEWPC", _ => { }, _ => Task.FromResult(false), CancellationToken.None));
    }

    [Fact]
    public async Task Cancelling_WhileTheCodeWaitsForConfirmation_EndsTheJoin()
    {
        using var host = StartHost(CommandSigner.NewKey(), request => request.Allow(), out var address);
        using var cancel = new CancellationTokenSource();
        var asked = new TaskCompletionSource();
        var never = new TaskCompletionSource<bool>();

        var join = Pairing.JoinAsync(address, "NEWPC", _ => { }, _ => { asked.TrySetResult(); return never.Task; }, cancel.Token);
        await asked.Task.WaitAsync(Step);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => join.WaitAsync(Step));
        // A late click on "The codes match" changes nothing.
        never.SetResult(true);
        Assert.True(join.IsCanceled || join.IsFaulted);
    }

    [Fact]
    public async Task ThisPc_IsAskedOnlyAfterTheHostAllowed()
    {
        var requested = new TaskCompletionSource<Pairing.JoinRequest>();
        using var host = StartHost(CommandSigner.NewKey(), request => requested.TrySetResult(request), out var address);
        var asked = false;

        var join = Pairing.JoinAsync(address, "NEWPC", _ => { }, _ => { asked = true; return Task.FromResult(true); }, CancellationToken.None);
        var request = await requested.Task.WaitAsync(Step);
        await Task.Delay(200);
        Assert.False(asked);

        request.Allow();
        await join.WaitAsync(Step);
        Assert.True(asked);
    }

    [Fact]
    public async Task ThisPc_IsNotAsked_WhenTheHostDenies()
    {
        using var host = StartHost(CommandSigner.NewKey(), request => request.Deny(), out var address);
        var asked = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Pairing.JoinAsync(address, "NEWPC", _ => { }, _ => { asked = true; return Task.FromResult(true); }, CancellationToken.None));
        Assert.False(asked);
    }

    [Fact]
    public async Task TheQuestion_SaysWhenThisPcWillBeManagedOneWay()
    {
        var ownKey = CommandSigner.NewKey();
        using var host = StartHost(CommandSigner.NewKey(), request => request.AllowManaged(ownKey), out var address);
        Pairing.JoinOffer? offer = null;

        var result = await Pairing.JoinAsync(address, "FRIENDPC", _ => { }, o => { offer = o; return Task.FromResult(true); }, CancellationToken.None);

        Assert.Equal(new Pairing.JoinOffer("HOSTPC", Managed: true), offer);
        Assert.True(result.Managed);
        Assert.Equal(ownKey, result.PairingKey);
    }
}

public class PairingHostLifetimeTests
{
    [Fact]
    public async Task AStoppedHost_DoesNotStart_AndLeavesThePortsFree()
    {
        var port = TestPorts.FreeRange(2);
        // "Add a PC" was closed before it started listening.
        var stopped = new Pairing.PairingHost(CommandSigner.NewKey(), "HOSTPC", port, port + 1);
        await stopped.StopAsync();

        Assert.Throws<ObjectDisposedException>(stopped.Start);

        using var next = new Pairing.PairingHost(CommandSigner.NewKey(), "HOSTPC", port, port + 1);
        next.Start();
    }

    [Fact]
    public void AHostThatCannotStart_KeepsNoPort()
    {
        var port = TestPorts.FreeRange(2);
        using var taken = new UdpClient(new IPEndPoint(IPAddress.Any, port + 1));
        using var host = new Pairing.PairingHost(CommandSigner.NewKey(), "HOSTPC", port, port + 1);

        Assert.Throws<SocketException>(host.Start);

        var tcp = new TcpListener(IPAddress.Any, port);
        tcp.Start();
        tcp.Stop();
    }
}
