using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Relctl;

/// <summary>
/// A strict reader over a YAML mapping. Every key the loader asks for is remembered; when the
/// loader calls <see cref="Done"/>, any key it never asked for is reported as unknown. That is
/// what turns a typo like `depends_on` or `verison` into an error instead of a silently ignored
/// setting.
/// </summary>
internal sealed class MapReader(YamlMappingNode node, string file, Diagnostics diagnostics, string what)
{
    private readonly HashSet<string> _asked = [];

    public int Line => (int)node.Start.Line;
    public string File => file;
    public Diagnostics Diagnostics => diagnostics;

    public static MapReader? FromDocument(string text, string file, Diagnostics diagnostics, string what)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException e)
        {
            diagnostics.Error(file, (int)e.Start.Line, $"isn't valid YAML: {e.InnerException?.Message ?? e.Message}");
            return null;
        }
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            diagnostics.Error(file, 1, $"should be a YAML mapping describing {what}.");
            return null;
        }
        return new MapReader(root, file, diagnostics, what);
    }

    public YamlNode? Node(string key)
    {
        _asked.Add(key);
        return node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
    }

    public int LineOf(string key) =>
        node.Children.Keys.FirstOrDefault(k => k is YamlScalarNode s && s.Value == key) is { } k ? (int)k.Start.Line : Line;

    public string? Str(string key, bool required = false)
    {
        var value = Node(key);
        if (value is null || value is YamlScalarNode { Value: null or "" or "~" or "null" })
        {
            if (required) diagnostics.Error(file, Line, $"{what} is missing `{key}`.");
            return null;
        }
        if (value is not YamlScalarNode scalar)
        {
            diagnostics.Error(file, (int)value.Start.Line, $"`{key}` should be a single value, not a list or mapping.");
            return null;
        }
        return scalar.Value;
    }

    public bool Bool(string key, bool fallback)
    {
        var text = Str(key);
        if (text is null) return fallback;
        if (bool.TryParse(text, out var b)) return b;
        diagnostics.Error(file, LineOf(key), $"`{key}` should be true or false, not \"{text}\".");
        return fallback;
    }

    public int Int(string key, int fallback)
    {
        var text = Str(key);
        if (text is null) return fallback;
        if (int.TryParse(text, out var i) && i > 0) return i;
        diagnostics.Error(file, LineOf(key), $"`{key}` should be a positive whole number, not \"{text}\".");
        return fallback;
    }

    public MapReader? Map(string key, bool required = false)
    {
        var value = Node(key);
        if (value is null)
        {
            if (required) diagnostics.Error(file, Line, $"{what} is missing `{key}`.");
            return null;
        }
        if (value is YamlScalarNode { Value: null or "" or "~" or "null" }) return null;
        if (value is not YamlMappingNode map)
        {
            diagnostics.Error(file, (int)value.Start.Line, $"`{key}` should be a mapping (key: value pairs).");
            return null;
        }
        return new MapReader(map, file, diagnostics, $"`{key}`");
    }

    public IReadOnlyList<YamlNode> Seq(string key)
    {
        var value = Node(key);
        if (value is null || value is YamlScalarNode { Value: null or "" or "~" or "null" }) return [];
        if (value is not YamlSequenceNode seq)
        {
            diagnostics.Error(file, (int)value.Start.Line, $"`{key}` should be a list (lines starting with `- `).");
            return [];
        }
        return seq.Children.ToList();
    }

    /// <summary>The key/value pairs of this mapping, for maps keyed by name (components, targets).</summary>
    public IEnumerable<(string Key, YamlNode Value, int Line)> Entries()
    {
        foreach (var (k, v) in node.Children)
        {
            var key = (k as YamlScalarNode)?.Value ?? "";
            _asked.Add(key);
            yield return (key, v, (int)k.Start.Line);
        }
    }

    public MapReader Child(YamlNode value, string childWhat) =>
        value is YamlMappingNode m
            ? new MapReader(m, file, diagnostics, childWhat)
            : throw new InvalidOperationException("Call IsMap first.");

    public bool IsMap(YamlNode value, string childWhat)
    {
        if (value is YamlMappingNode) return true;
        diagnostics.Error(file, (int)value.Start.Line, $"{childWhat} should be a mapping (key: value pairs).");
        return false;
    }

    /// <summary>Report every key nobody asked for. Call after reading all the keys you allow.</summary>
    public void Done()
    {
        foreach (var k in node.Children.Keys)
        {
            var key = (k as YamlScalarNode)?.Value ?? "";
            if (!_asked.Contains(key))
                diagnostics.Error(file, (int)k.Start.Line,
                    $"unknown key `{key}` in {what}. Allowed: {string.Join(", ", _asked.Order())}.");
        }
    }
}
