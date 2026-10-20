using System.Reflection;
using Google.Protobuf;
using OpenAPI.Net.Helpers;

namespace Trader.Core.OpenApi;

/// <summary>
/// Decodes a raw Open API envelope into its message. Uses the library's MessageFactory first; when that
/// returns nothing — the 2022 library has no mapping for some messages, e.g. ProtoOASubscribeLiveTrendbarRes
/// (found in the Audit Bot's step 3, open-code-pieces.md §3) — it falls back to a table built from every generated message class
/// in the library: each class knows its own payload type and has a parser.
/// </summary>
public static class PayloadDecoder
{
    private static readonly Lazy<Dictionary<uint, MessageParser>> Parsers = new(BuildTable);

    /// <summary>The message inside <paramref name="envelope"/>, or null if its payload type is unknown.</summary>
    public static IMessage? Decode(ProtoMessage envelope) =>
        MessageFactory.GetMessage(envelope) ?? DecodeWithTable(envelope);

    /// <summary>True when the library itself cannot decode this envelope (so it will not deliver a decoded copy).</summary>
    public static bool LibraryCannotDecode(ProtoMessage envelope) => MessageFactory.GetMessage(envelope) is null;

    internal static IMessage? DecodeWithTable(ProtoMessage envelope) =>
        Parsers.Value.TryGetValue(envelope.PayloadType, out var parser) ? parser.ParseFrom(envelope.Payload) : null;

    internal static int KnownPayloadTypes => Parsers.Value.Count;

    private static Dictionary<uint, MessageParser> BuildTable()
    {
        var table = new Dictionary<uint, MessageParser>();
        foreach (var type in typeof(ProtoOAApplicationAuthReq).Assembly.GetTypes())
        {
            if (!typeof(IMessage).IsAssignableFrom(type) || type.IsAbstract ||
                type.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not MessageParser parser ||
                type.GetProperty("PayloadType", BindingFlags.Public | BindingFlags.Instance) is not { } payloadProperty)
                continue;

            // A new instance holds the default payload type for its class (set in the .proto file).
            var value = payloadProperty.GetValue(Activator.CreateInstance(type));
            if (value is Enum e)
                table.TryAdd(Convert.ToUInt32(e), parser);
        }
        return table;
    }
}
