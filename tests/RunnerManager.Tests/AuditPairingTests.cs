using System.Net;
using System.Net.Sockets;

namespace RunnerManager.Tests;

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
