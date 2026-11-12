using System.Text.RegularExpressions;

namespace WebForms.Designer;

/// <summary>Reads the semantic content back out of an existing designer file (any IDE's formatting).</summary>
public sealed partial class ExistingDesigner
{
    public string? ClassName { get; private init; }
    public string? Namespace { get; private init; }
    public required List<(string Name, string Type)> Fields { get; init; }
    public string? MasterTypeName { get; private init; }

    [GeneratedRegex(@"^\s*Namespace\s+(?<ns>[\w.]+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex NamespaceRegex();

    [GeneratedRegex(@"^\s*Partial\s+(?:(?:Public|Friend|Private|Protected)\s+)*(?:(?:Public|Friend|Private|Protected)\s+)?Class\s+(?<name>\[?\w+\]?)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ClassRegex();

    [GeneratedRegex(@"^\s*(?:(?:Protected|Public|Friend|Private)\s+)?WithEvents\s+(?<name>\[?\w+\]?)\s+As\s+(?<type>[\w.]+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex FieldRegex();

    [GeneratedRegex(@"ReadOnly\s+Property\s+Master\s*\(\s*\)\s+As\s+(?<type>[\w.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MasterRegex();

    public static ExistingDesigner Parse(string text)
    {
        var fields = new List<(string, string)>();
        foreach (Match m in FieldRegex().Matches(text))
        {
            fields.Add((m.Groups["name"].Value.Trim('[', ']'), m.Groups["type"].Value));
        }

        var cls = ClassRegex().Match(text);
        var ns = NamespaceRegex().Match(text);
        var master = MasterRegex().Match(text);
        return new ExistingDesigner
        {
            ClassName = cls.Success ? cls.Groups["name"].Value.Trim('[', ']') : null,
            Namespace = ns.Success ? ns.Groups["ns"].Value : null,
            Fields = fields,
            MasterTypeName = master.Success ? master.Groups["type"].Value : null,
        };
    }
}
