using System.Text.Json.Nodes;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Configuration;

/// <summary>
/// Tool results are serialised with Newtonsoft.Json, which cannot serialise System.Text.Json nodes:
/// JsonValue exposes Parent/Root, so the serializer hits a self-referencing loop, the response is never
/// written and the client sees the pipe close. Convert config nodes before putting them in Data.
/// </summary>
internal static class JsonNodeConversion
{
    public static JToken? ToJToken(JsonNode? node) => node == null ? null : JToken.Parse(node.ToJsonString());
}
