using WebForms.Markup;

namespace WebForms.Designer;

public enum Severity
{
    Info,
    Warning,
    Error,
}

/// <summary>A diagnostic in MSBuild canonical format: <c>file(line,col): error WF1001: message</c>.</summary>
public sealed record Diagnostic(string File, int Line, int Column, Severity Severity, string Code, string Message)
{
    public override string ToString() =>
        $"{File}({Line},{Column}): {Severity.ToString().ToLowerInvariant()} {Code}: {Message}";
}

public sealed record DesignerField(string Name, string TypeName, SourcePosition Position);

/// <summary>The semantic content of a designer file.</summary>
public sealed record DesignerModel
{
    public required string ClassName { get; init; }

    /// <summary>Namespace relative to the project root namespace (null when the class is directly in the root namespace).</summary>
    public string? Namespace { get; init; }

    public required IReadOnlyList<DesignerField> Fields { get; init; }

    /// <summary>Type of the strongly typed Master property, as written (no Global. prefix), or null.</summary>
    public string? MasterTypeName { get; init; }

    /// <summary>
    /// True when <see cref="MasterTypeName"/> comes from a <c>&lt;%@ MasterType %&gt;</c> directive (Visual Studio then
    /// writes the Master property); false when it only comes from the MasterPageFile (only Rider writes it then).
    /// </summary>
    public bool MasterTypeDeclared { get; init; }
}

public sealed class GenerationResult
{
    public required string MarkupPath { get; init; }
    public string? DesignerPath { get; init; }
    public string? CodeBehindPath { get; init; }
    public DesignerModel? Model { get; init; }

    /// <summary>The parsed markup, with <see cref="ResolvedControl"/> / <see cref="PropertyAnnotation"/> annotations (null when skipped or when the directives are unusable).</summary>
    public MarkupDocument? Document { get; init; }

    public List<Diagnostic> Diagnostics { get; } = [];

    /// <summary>Set when the file has no designer (no CodeBehind attribute, e.g. CodeFile / single-file pages).</summary>
    public string? SkipReason { get; init; }

    public bool HasErrors => Diagnostics.Any(d => d.Severity == Severity.Error);
}

public static class DesignerGenerator
{
    private const string ContentType = "System.Web.UI.WebControls.Content";

    public static bool IsMarkupFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".aspx", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".ascx", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".master", StringComparison.OrdinalIgnoreCase);
    }

    public static GenerationResult Generate(string markupPath)
    {
        markupPath = Path.GetFullPath(markupPath);
        string text = File.ReadAllText(markupPath);
        var project = ProjectContext.ForFile(markupPath);
        return Generate(markupPath, text, project);
    }

    /// <param name="scanCodeBehind">
    /// Reads the members of the code-behind class (path, class name). Defaults to scanning the file on disk; the
    /// language server passes a cached scanner that also sees unsaved editor buffers.
    /// </param>
    public static GenerationResult Generate(string markupPath, string text, ProjectContext project, Func<string, string, CodeBehindInfo>? scanCodeBehind = null)
    {
        var directivesDoc = MarkupDocument.ParseDirectivesOnly(text);
        var main = directivesDoc.MainDirective;
        string displayPath = markupPath;

        string? codeBehind = main?.GetAttributeValue("CodeBehind")?.Trim();
        if (main is null || string.IsNullOrEmpty(codeBehind))
        {
            string reason = main is null
                ? "no Page/Control/Master directive"
                : main.GetAttributeValue("CodeFile") is not null ? "uses CodeFile (web site model, no designer file)" : "no CodeBehind attribute";
            return new GenerationResult { MarkupPath = markupPath, SkipReason = reason };
        }

        var dir = Path.GetDirectoryName(markupPath)!;
        string designerPath = markupPath + ".designer.vb";
        string codeBehindPath = Path.Combine(dir, codeBehind);

        string? inherits = main.GetAttributeValue("Inherits")?.Trim();
        if (string.IsNullOrEmpty(inherits))
        {
            var r = new GenerationResult { MarkupPath = markupPath, DesignerPath = designerPath, CodeBehindPath = codeBehindPath };
            r.Diagnostics.Add(Diag(displayPath, main.Start, Severity.Error, "WF1001", $"<%@ {main.Name} %> has no Inherits attribute; cannot determine the designer class."));
            return r;
        }

        var schema = new ControlSchema(project, markupPath, PageRegistrations(directivesDoc));
        var doc = MarkupDocument.Parse(directivesDoc, schema);
        var diagnostics = new List<Diagnostic>();

        foreach (var d in doc.Diagnostics)
        {
            diagnostics.Add(Diag(displayPath, d.Position, d.IsError ? Severity.Error : Severity.Warning, "WF2001", d.Message));
        }

        // Class name and namespace: the Inherits type relative to the project's root namespace
        // (VB prepends the root namespace to every namespace declared in source).
        string root = project.RootNamespace;
        int lastDot = inherits.LastIndexOf('.');
        string className = lastDot < 0 ? inherits : inherits[(lastDot + 1)..];
        var codeBehindInfo = (scanCodeBehind ?? CodeBehindScanner.Scan)(codeBehindPath, className);
        string? ns;
        if (string.IsNullOrEmpty(root) || inherits.StartsWith(root + ".", StringComparison.Ordinal))
        {
            string relative = string.IsNullOrEmpty(root) ? inherits : inherits[(root.Length + 1)..];
            int dot = relative.LastIndexOf('.');
            ns = dot < 0 ? null : relative[..dot];
        }
        else
        {
            // The designer partial class must match the code-behind class, so follow the code-behind.
            ns = codeBehindInfo.Namespace;
            diagnostics.Add(Diag(displayPath, main.Start, Severity.Warning, "WF1002",
                $"Inherits '{inherits}' is not in the project root namespace '{root}' (the code-behind class is '{root}.{(ns is null ? "" : ns + ".")}{className}'); ASP.NET will not find this type at runtime."));
        }

        if (!codeBehindInfo.ClassFound && File.Exists(codeBehindPath))
        {
            diagnostics.Add(Diag(displayPath, main.Start, Severity.Warning, "WF1003",
                $"Class '{className}' not found in code-behind '{Path.GetFileName(codeBehindPath)}'."));
        }

        // Fields.
        var declared = codeBehindInfo.Members;
        var fields = new List<DesignerField>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectFields(doc.Roots, inMultiTemplate: false, fields, seen, declared, diagnostics, displayPath);

        // Master property.
        var (masterType, masterTypeDeclared) = ResolveMasterType(directivesDoc, main, project, markupPath, diagnostics, displayPath);
        if (masterType is not null && declared.Contains("Master"))
        {
            masterType = null;
        }

        var result = new GenerationResult
        {
            MarkupPath = markupPath,
            DesignerPath = designerPath,
            CodeBehindPath = codeBehindPath,
            Document = doc,
            Model = new DesignerModel
            {
                ClassName = className,
                Namespace = ns,
                Fields = fields,
                MasterTypeName = masterType,
                MasterTypeDeclared = masterType is not null && masterTypeDeclared,
            },
        };
        result.Diagnostics.AddRange(diagnostics);
        return result;
    }

    /// <summary>The page's own <c>&lt;%@ Register %&gt;</c> directives, in order (they take precedence over web.config).</summary>
    public static List<TagRegistration> PageRegistrations(MarkupDocument directives) => PageRegistrations(directives.Directives);

    /// <summary>The Register directives among <paramref name="directives"/> (other tokens are ignored).</summary>
    public static List<TagRegistration> PageRegistrations(IEnumerable<MarkupToken> directives)
    {
        var registrations = new List<TagRegistration>();
        foreach (var reg in directives.Where(t => t.Kind == TokenKind.Directive && t.Name.Equals("Register", StringComparison.OrdinalIgnoreCase)))
        {
            registrations.Add(new TagRegistration(
                reg.GetAttributeValue("TagPrefix")?.Trim() ?? "",
                reg.GetAttributeValue("Namespace")?.Trim(),
                reg.GetAttributeValue("Assembly")?.Trim(),
                reg.GetAttributeValue("TagName")?.Trim(),
                reg.GetAttributeValue("Src")?.Trim(),
                "page"));
        }

        return registrations;
    }

    private static void CollectFields(
        IReadOnlyList<MarkupElement> elements,
        bool inMultiTemplate,
        List<DesignerField> fields,
        HashSet<string> seen,
        HashSet<string> declared,
        List<Diagnostic> diagnostics,
        string displayPath)
    {
        foreach (var el in elements)
        {
            bool childMulti = inMultiTemplate;
            if (el.Annotation is PropertyAnnotation pa && pa.IsMultiInstanceTemplate)
            {
                childMulti = true;
            }

            if (el.Annotation is ResolvedControl rc)
            {
                if (rc.Error is not null && (el.IsRunatServer || el.Role == ElementRole.Item))
                {
                    // Only an error if we would have to emit a field for it.
                    bool needsField = !inMultiTemplate && IsStaticId(el.Id);
                    diagnostics.Add(Diag(displayPath, el.StartTag.NamePosition,
                        needsField && rc.DesignerTypeName is null ? Severity.Error : Severity.Warning,
                        rc.IsUserControl ? "WF3002" : "WF3001", rc.Error));
                }

                if (!inMultiTemplate && IsStaticId(el.Id) && (rc.IsControl || rc.Type is null) && rc.Type?.FullName != ContentType)
                {
                    string id = el.Id!.Trim();
                    if (rc.DesignerTypeName is not null && !declared.Contains(id) && seen.Add(id))
                    {
                        fields.Add(new DesignerField(id, rc.DesignerTypeName, el.Position));
                    }
                }
            }

            if (el.Children.Count > 0)
            {
                CollectFields(el.Children, childMulti, fields, seen, declared, diagnostics, displayPath);
            }
        }
    }

    private static bool IsStaticId(string? id) => !string.IsNullOrWhiteSpace(id) && !id.Contains("<%", StringComparison.Ordinal);

    /// <returns>The master type, and whether it was declared with a MasterType directive.</returns>
    private static (string? Type, bool Declared) ResolveMasterType(MarkupDocument directives, MarkupToken main, ProjectContext project, string markupPath, List<Diagnostic> diagnostics, string displayPath)
    {
        if (!main.Name.Equals("Page", StringComparison.OrdinalIgnoreCase) && !main.Name.Equals("Master", StringComparison.OrdinalIgnoreCase))
        {
            return (null, false);
        }

        var masterTypeDirective = directives.GetDirectives("MasterType").FirstOrDefault();
        if (masterTypeDirective is not null)
        {
            // Code relies on the property when MasterType is declared, so failing to resolve it is an error.
            var typeName = masterTypeDirective.GetAttributeValue("TypeName")?.Trim();
            if (!string.IsNullOrEmpty(typeName))
            {
                return (typeName, true);
            }

            var vp = masterTypeDirective.GetAttributeValue("VirtualPath")?.Trim();
            if (!string.IsNullOrEmpty(vp))
            {
                return (InheritsOf(vp, masterTypeDirective, Severity.Error), true);
            }

            diagnostics.Add(Diag(displayPath, masterTypeDirective.Start, Severity.Error, "WF3005", "<%@ MasterType %> needs a TypeName or VirtualPath attribute."));
            return (null, true);
        }

        var masterFile = main.GetAttributeValue("MasterPageFile")?.Trim();
        if (string.IsNullOrEmpty(masterFile) || masterFile.Contains("<%", StringComparison.Ordinal))
        {
            return (null, false);
        }

        return (InheritsOf(masterFile, main, Severity.Warning), false);

        string? InheritsOf(string virtualPath, MarkupToken directive, Severity severity)
        {
            var path = project.ResolveVirtualPath(virtualPath, markupPath);
            if (!File.Exists(path))
            {
                diagnostics.Add(Diag(displayPath, directive.Start, severity, "WF3003", $"Master page '{virtualPath}' not found ({path}); no Master property generated."));
                return null;
            }

            var inherits = project.GetInherits(path);
            if (string.IsNullOrEmpty(inherits))
            {
                diagnostics.Add(Diag(displayPath, directive.Start, severity, "WF3004", $"Master page '{virtualPath}' has no Inherits attribute; no Master property generated."));
            }

            return string.IsNullOrEmpty(inherits) ? null : inherits;
        }
    }

    internal static Diagnostic Diag(string file, SourcePosition pos, Severity sev, string code, string message) =>
        new(file, pos.Line, pos.Column, sev, code, message);
}
