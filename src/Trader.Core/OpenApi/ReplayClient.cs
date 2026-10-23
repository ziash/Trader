using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using OpenAPI.Net;
using OpenAPI.Net.Helpers;

namespace Trader.Core.OpenApi;

/// <summary>
/// The connection to a replay broker on this computer (Rewind) when the secrets file names its certificate authority
/// (CTRADER_CA_FILE, open-code-pieces.md §3). Spotware's OpenClient accepts only certificates the operating system trusts
/// and offers no way to add one; on Linux SSL_CERT_FILE works around that, on Windows nothing does. This client trusts
/// exactly that one authority — not the system's list — and otherwise does what OpenClient does over TCP: each message
/// is a 4-byte big-endian length and a ProtoMessage, a heartbeat goes out when nothing was sent for a while, and
/// observers get messages in the same form and order. No request pacing: the broker is on this computer.
/// </summary>
internal sealed class ReplayClient(string host, int port, string caFile, TimeSpan heartbeat) : IOpenApiClient
{
    private readonly ConcurrentDictionary<object, IObserver<IMessage>> _observers = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private X509Certificate2? _ca;
    private TcpClient? _tcp;
    private SslStream? _ssl;
    private Timer? _heartbeatTimer;
    private long _lastSentTicks = DateTime.UtcNow.Ticks;
    private int _released;
    private int _ended; // 1 once OnError or OnCompleted went to the observers

    public string Host => host;

    public async Task Connect()
    {
        try
        {
            _ca = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(caFile));
            _tcp = new TcpClient();
            await _tcp.ConnectAsync(host, port, _cts.Token);
            _ssl = new SslStream(_tcp.GetStream(), leaveInnerStreamOpen: false);
            await _ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                CertificateChainPolicy = new X509ChainPolicy
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    CustomTrustStore = { _ca },
                    RevocationMode = X509RevocationMode.NoCheck, // a private authority on this computer has no revocation list
                },
            }, _cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IOException($"Could not connect to the replay broker at {host}:{port} with the authority in {caFile}: {ex.Message}", ex);
        }

        _ = Task.Run(ReadAsync);
        _heartbeatTimer = new Timer(_ => _ = SendHeartbeatAsync(), null, heartbeat, heartbeat);
    }

    public Task SendMessage(IMessage message, string clientMsgId) =>
        WriteAsync(MessageFactory.GetMessage(message.GetPayloadType(), message.ToByteString(), clientMsgId));

    public IDisposable Subscribe(IObserver<IMessage> observer)
    {
        var key = new object();
        _observers[key] = observer;
        return new Unsubscriber(() => _observers.TryRemove(key, out _));
    }

    public void Dispose()
    {
        Release();
        if (Interlocked.Exchange(ref _ended, 1) == 0)
            foreach (var observer in _observers.Values)
                observer.OnCompleted();
    }

    private async Task ReadAsync()
    {
        var header = new byte[4];
        try
        {
            while (true)
            {
                await _ssl!.ReadExactlyAsync(header, _cts.Token);
                var length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length <= 0)
                    continue;
                var data = new byte[length];
                await _ssl.ReadExactlyAsync(data, _cts.Token);
                Deliver(ProtoMessage.Parser.ParseFrom(data));
            }
        }
        catch (EndOfStreamException)
        {
            Fail(new IOException("Remote host closed the connection"));
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    // As OpenClient: the envelope when it carries a clientMsgId or cannot be decoded, then the decoded message if any.
    private void Deliver(ProtoMessage envelope)
    {
        var decoded = MessageFactory.GetMessage(envelope);
        foreach (var observer in _observers.Values)
        {
            if (envelope.HasClientMsgId || decoded is null)
                observer.OnNext(envelope);
            if (decoded is not null)
                observer.OnNext(decoded);
        }
    }

    private async Task SendHeartbeatAsync()
    {
        if (DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastSentTicks), DateTimeKind.Utc) < heartbeat)
            return;
        try
        {
            await WriteAsync(new ProtoHeartbeatEvent().GetMessage(ProtoPayloadType.HeartbeatEvent));
        }
        catch
        {
            // WriteAsync has already ended the connection.
        }
    }

    private async Task WriteAsync(ProtoMessage message)
    {
        var body = message.ToByteArray();
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        try
        {
            await _writeLock.WaitAsync(_cts.Token);
            try
            {
                await _ssl!.WriteAsync(frame, _cts.Token);
                await _ssl.FlushAsync(_cts.Token);
                Interlocked.Exchange(ref _lastSentTicks, DateTime.UtcNow.Ticks);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
            throw new IOException($"Could not send {message.PayloadType} to the replay broker", ex);
        }
    }

    private void Fail(Exception error)
    {
        Release();
        if (Interlocked.Exchange(ref _ended, 1) == 0)
            foreach (var observer in _observers.Values)
                observer.OnError(error);
    }

    private void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 1)
            return;
        _heartbeatTimer?.Dispose();
        _cts.Cancel();
        _ssl?.Dispose();
        _tcp?.Dispose();
        _ca?.Dispose();
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
