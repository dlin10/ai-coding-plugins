using Google.Protobuf;
using Google.Protobuf.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ProtobufParserFactoryAtParse;

/// <summary>The counter the parser's factory increments.</summary>
public static class Tally
{
    public static int Created;
}

/// <summary>A message of the case's own, with the parser a generated message has: its factory counts every message it makes, and
/// <c>ParseFrom</c> calls the factory.</summary>
public sealed class Msg : IMessage<Msg>
{
    public static MessageParser<Msg> Parser { get; } = new MessageParser<Msg>(() =>
    {
        Tally.Created++;
        return new Msg();
    });

    public MessageDescriptor Descriptor => throw new NotSupportedException();

    public int CalculateSize() => 0;

    public Msg Clone() => new();

    public bool Equals(Msg? other) => ReferenceEquals(this, other);

    public void MergeFrom(CodedInputStream input)
    {
    }

    public void MergeFrom(Msg message)
    {
    }

    public void WriteTo(CodedOutputStream output)
    {
    }
}

[ApiController]
[Route("cases/protobuf-parser-factory-at-parse")]
public sealed class MsgController : ControllerBase
{
    [HttpPost]
    public void Post(byte[] body) => Msg.Parser.ParseFrom(body);
}
