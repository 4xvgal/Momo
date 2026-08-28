using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Momo.Payments;

public class LnurlHttpHandlerFactory
{
    private readonly bool _allowLoopback;

    internal LnurlHttpHandlerFactory(bool allowLoopback)
    => _allowLoopback = allowLoopback;

    public static HttpMessageHandler Create(bool allowLoopback = false)
    {
        var factory = new LnurlHttpHandlerFactory(allowLoopback); // instance
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = factory.ConnectAsync,
        };
    }

    internal IPAddress SelectPublicIp(IPAddress[] addresses)
    => addresses.FirstOrDefault(a => !LnurlClient.IsPrivateOrLoopback(a) || _allowLoopback)
        ?? throw new InvalidOperationException($"Blocked IP for host");

    private async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;

        // 1) only one dns query
        var addresses = await Dns.GetHostAddressesAsync(host, ct);

        // 2) select only non private IP
        var ip = SelectPublicIp(addresses);

        // 3) connect tcp with public ip
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(ip, ctx.DnsEndPoint.Port, ct);

        var stream = new NetworkStream(socket, ownsSocket: true);

        // TLS only for https; plain http (tests, loopback) must stay plain
        var isHttps = string.Equals(ctx.InitialRequestMessage.RequestUri.Scheme,
            Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        if (!isHttps) return stream;

        // 4) TLS: verify cert with origin domain header
        var ssl = new SslStream(stream);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, ct);

        return ssl;
    }

}
