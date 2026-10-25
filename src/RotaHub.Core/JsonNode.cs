using System.Collections;
using System.Globalization;
using System.Web.Script.Serialization;

namespace RotaHub.Core;

/// <summary>
/// Read-only view over JSON parsed by .NET Framework's own JavaScriptSerializer, so RotaHub needs
/// no JSON package and stays a single exe. Missing members read as empty instead of throwing.
/// </summary>
public readonly struct JsonNode
{
    internal static readonly JavaScriptSerializer Serializer = new() { MaxJsonLength = int.MaxValue };

    private readonly object? _value;

    public JsonNode(object? value) => _value = value;

    public static JsonNode Parse(string? text) =>
        string.IsNullOrWhiteSpace(text) ? default : new JsonNode(Serializer.DeserializeObject(text));

    public static string Write(object value) => Serializer.Serialize(value);

    public static T? Read<T>(string text) where T : class => Serializer.Deserialize<T>(text);

    public JsonNode this[string name] =>
        _value is IDictionary<string, object> d && d.TryGetValue(name, out var v) ? new JsonNode(v) : default;

    public bool Exists => _value != null;

    public string Str(string fallback = "") => _value switch
    {
        null => fallback,
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => fallback
    };

    public long Long(long fallback = 0) => _value switch
    {
        int i => i,
        long l => l,
        decimal m => (long)m,
        double d => (long)d,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => fallback
    };

    public int Int(int fallback = 0) => (int)Long(fallback);

    public bool Bool() => _value switch
    {
        bool b => b,
        int i => i != 0,
        string s => s == "true" || s == "1",
        _ => false
    };

    public IEnumerable<JsonNode> Items => _value is IEnumerable list and not string and not IDictionary
        ? list.Cast<object?>().Select(v => new JsonNode(v))
        : Enumerable.Empty<JsonNode>();

    public IEnumerable<string> Strings => Items.Select(i => i.Str()).Where(s => s.Length > 0);
}
