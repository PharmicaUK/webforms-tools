using System.Collections.Concurrent;
using System.Text;
using System.Xml;

namespace WebForms.LanguageServer.Completion;

/// <summary>
/// Summaries from the XML documentation files next to assemblies (the .NET Framework reference assemblies ship
/// them: <c>System.Web.xml</c>). Each file is read once, on the first lookup into it: only completion item resolve
/// needs them, so the first completion stays fast.
/// </summary>
public static class XmlDocumentation
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The summary of a member id (<c>T:System.Web.UI.WebControls.Button</c>, <c>P:…Button.Text</c>) or null.</summary>
    public static string? Summary(string? assemblyPath, string memberId)
    {
        if (assemblyPath is null)
        {
            return null;
        }

        var xml = Path.ChangeExtension(assemblyPath, ".xml");
        return Files.GetOrAdd(xml, Load).GetValueOrDefault(memberId);
    }

    private static IReadOnlyDictionary<string, string> Load(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true });
            string? member = null;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.Name == "member")
                {
                    member = reader.GetAttribute("name");
                }
                else if (reader.Name == "summary" && member is not null)
                {
                    result.TryAdd(member, ReadText(reader));
                    member = null;
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            // An unreadable doc file only means no documentation.
        }

        return result;
    }

    /// <summary>The text of the current element; <c>&lt;see cref="T:X.Y"/&gt;</c> becomes <c>Y</c>, whitespace is collapsed.</summary>
    private static string ReadText(XmlReader reader)
    {
        var sb = new StringBuilder();
        if (reader.IsEmptyElement)
        {
            return "";
        }

        int depth = reader.Depth;
        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    sb.Append(reader.Value);
                    break;
                case XmlNodeType.Element when reader.Name is "see" or "seealso":
                    var cref = reader.GetAttribute("cref") ?? reader.GetAttribute("langword") ?? "";
                    var name = cref.Contains(':', StringComparison.Ordinal) ? cref[(cref.IndexOf(':', StringComparison.Ordinal) + 1)..] : cref;
                    name = name.Split('(')[0];
                    sb.Append('`').Append(name[(name.LastIndexOf('.') + 1)..]).Append('`');
                    break;
                case XmlNodeType.Element when reader.Name is "paramref" or "typeparamref":
                    sb.Append('`').Append(reader.GetAttribute("name")).Append('`');
                    break;
            }
        }

        var collapsed = new StringBuilder(sb.Length);
        bool space = false;
        foreach (char c in sb.ToString())
        {
            if (char.IsWhiteSpace(c))
            {
                space = collapsed.Length > 0;
                continue;
            }

            if (space)
            {
                collapsed.Append(' ');
                space = false;
            }

            collapsed.Append(c);
        }

        return collapsed.ToString();
    }
}
