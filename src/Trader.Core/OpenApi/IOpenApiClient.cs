using Google.Protobuf;
using OpenAPI.Net;

namespace Trader.Core.OpenApi;

/// <summary>
/// The connection under <see cref="CTraderSession"/>: Spotware's OpenClient (demo, and replay without a CA file), or
/// <see cref="ReplayClient"/> for a replay broker whose certificate authority the secrets file names (open-code-pieces.md §3).
/// Observers get messages exactly as OpenClient delivers them, then OnError or OnCompleted once.
/// </summary>
internal interface IOpenApiClient : IObservable<IMessage>, IDisposable
{
    string Host { get; }

    Task Connect();

    Task SendMessage(IMessage message, string clientMsgId);
}

/// <summary>Spotware's OpenClient as an <see cref="IOpenApiClient"/>; trusts the certificates the operating system trusts.</summary>
internal sealed class SpotwareClient(OpenClient client) : IOpenApiClient
{
    public string Host => client.Host;

    public Task Connect() => client.Connect();

    public Task SendMessage(IMessage message, string clientMsgId) => client.SendMessage(message, clientMsgId);

    public IDisposable Subscribe(IObserver<IMessage> observer) => client.Subscribe(observer);

    public void Dispose() => client.Dispose();
}
