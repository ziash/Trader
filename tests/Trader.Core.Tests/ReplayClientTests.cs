using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using OpenAPI.Net;
using OpenAPI.Net.Helpers;
using Trader.Core.OpenApi;

namespace Trader.Core.Tests;

/// <summary>ReplayClient against a small TLS server on localhost whose certificate comes from its own authority, as Rewind's does.</summary>
public sealed class ReplayClientTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("replay-client-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Trusts_the_named_authority_and_exchanges_messages()
    {
        var (caFile, server) = MakeCertificates("good");
        await using var broker = new EchoBroker(server);
        var observer = new Recorder();
        using var client = new ReplayClient("localhost", broker.Port, caFile, TimeSpan.FromSeconds(5));
        using var _ = client.Subscribe(observer);

        await client.Connect();
        await client.SendMessage(new ProtoOAVersionReq(), "req-1");

        var reply = await observer.Decoded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("replay-1", Assert.IsType<ProtoOAVersionRes>(reply).Version);
        Assert.Contains(observer.Messages, m => m is ProtoMessage { ClientMsgId: "req-1" }); // the envelope, as OpenClient gives it
    }

    [Fact]
    public async Task Refuses_a_server_from_another_authority()
    {
        var (_, server) = MakeCertificates("server");
        var (otherCa, _) = MakeCertificates("other");
        await using var broker = new EchoBroker(server);
        using var client = new ReplayClient("localhost", broker.Port, otherCa, TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<IOException>(client.Connect);
    }

    [Fact]
    public async Task Reports_the_end_of_the_connection_once()
    {
        var (caFile, server) = MakeCertificates("good");
        var broker = new EchoBroker(server);
        var observer = new Recorder();
        using var client = new ReplayClient("localhost", broker.Port, caFile, TimeSpan.FromSeconds(5));
        using var _ = client.Subscribe(observer);
        await client.Connect();

        await broker.DisposeAsync();

        Assert.IsType<IOException>(await observer.Error.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        client.Dispose();
        Assert.Equal(0, observer.Completed);
    }

    /// <summary>An authority (written as PEM, like Rewind's ca.pem) and a localhost certificate it signed.</summary>
    private (string CaFile, X509Certificate2 Server) MakeCertificates(string name)
    {
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caReq = new CertificateRequest($"CN=Test CA {name}", caKey, HashAlgorithmName.SHA256);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var ca = caReq.CreateSelfSigned(now, now.AddYears(1));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        req.CertificateExtensions.Add(san.Build());
        using var signed = req.Create(ca, now, now.AddDays(30), RandomNumberGenerator.GetBytes(8));
        using var withKey = signed.CopyWithPrivateKey(key);

        var caFile = Path.Combine(_dir, $"{name}-ca.pem");
        File.WriteAllText(caFile, ca.ExportCertificatePem());
        return (caFile, X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null));
    }

    /// <summary>Accepts one connection and answers every request with a ProtoOAVersionRes under the same clientMsgId.</summary>
    private sealed class EchoBroker : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _run;

        public EchoBroker(X509Certificate2 certificate)
        {
            _listener.Start();
            _run = RunAsync(certificate);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task RunAsync(X509Certificate2 certificate)
        {
            try
            {
                using var tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                await using var ssl = new SslStream(tcp.GetStream());
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, _cts.Token);
                var header = new byte[4];
                while (true)
                {
                    await ssl.ReadExactlyAsync(header, _cts.Token);
                    var data = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
                    await ssl.ReadExactlyAsync(data, _cts.Token);
                    var request = ProtoMessage.Parser.ParseFrom(data);
                    if (request.PayloadType == (uint)ProtoPayloadType.HeartbeatEvent)
                        continue;
                    var body = new ProtoOAVersionRes { Version = "replay-1" }.GetMessage(ProtoOAPayloadType.ProtoOaVersionRes, request.ClientMsgId).ToByteArray();
                    var frame = new byte[4 + body.Length];
                    BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
                    body.CopyTo(frame, 4);
                    await ssl.WriteAsync(frame, _cts.Token);
                }
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
            {
                // the client refused the certificate or went away
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            await _run;
        }
    }

    private sealed class Recorder : IObserver<IMessage>
    {
        public ConcurrentQueue<IMessage> Messages { get; } = new();
        public TaskCompletionSource<IMessage> Decoded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Exception> Error { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Completed;

        public void OnNext(IMessage value)
        {
            Messages.Enqueue(value);
            if (value is not ProtoMessage)
                Decoded.TrySetResult(value);
        }

        public void OnError(Exception error) => Error.TrySetResult(error);

        public void OnCompleted() => Interlocked.Increment(ref Completed);
    }
}
