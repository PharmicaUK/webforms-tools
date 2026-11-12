using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using WebForms.Designer;

/// <summary>
/// Cross-checks the lightweight <see cref="CodeBehindScanner"/> against Roslyn's VB parser on every class in
/// every .vb file of the corpus: same class-level member names (fields, properties, events) and namespace.
/// </summary>
internal static class ScannerVerifier
{
    public static int Run(string corpus)
    {
        int classes = 0, mismatches = 0, files = 0;
        foreach (var file in Directory.EnumerateFiles(corpus, "*.vb", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(corpus, file);
            if (rel.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or "node_modules" or ".git" or ".claude" or "packages") ||
                rel.EndsWith(".designer.vb", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            files++;
            var text = File.ReadAllText(file);
            var root = VisualBasicSyntaxTree.ParseText(text).GetRoot();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cls in root.DescendantNodes().OfType<ClassBlockSyntax>())
            {
                var name = cls.ClassStatement.Identifier.ValueText;
                if (cls.ClassStatement.TypeParameterList is not null || !seenNames.Add(name) || cls.Parent is ClassBlockSyntax)
                {
                    continue; // the scanner reports the first top-level class of a name, like the generator needs
                }

                classes++;
                var expected = RoslynMembers(root, name, out var expectedNs);
                var actual = CodeBehindScanner.ScanText(text, name);
                var missing = expected.Except(actual.Members, StringComparer.OrdinalIgnoreCase).ToList();
                var extra = actual.Members.Except(expected, StringComparer.OrdinalIgnoreCase).ToList();
                bool nsOk = string.Equals(expectedNs, actual.Namespace, StringComparison.OrdinalIgnoreCase);
                if (missing.Count > 0 || extra.Count > 0 || !nsOk || !actual.ClassFound)
                {
                    mismatches++;
                    Console.WriteLine($"{rel} :: {name}: found={actual.ClassFound} ns={actual.Namespace ?? "-"}/{expectedNs ?? "-"} missing=[{string.Join(", ", missing)}] extra=[{string.Join(", ", extra)}]");
                }
            }
        }

        Console.WriteLine($"Scanner check: {files} files, {classes} classes, {mismatches} mismatches.");
        return mismatches == 0 ? 0 : 1;
    }

    private static HashSet<string> RoslynMembers(Microsoft.CodeAnalysis.SyntaxNode root, string className, out string? ns)
    {
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ns = null;
        bool found = false;
        foreach (var cls in root.DescendantNodes().OfType<ClassBlockSyntax>())
        {
            if (cls.Parent is ClassBlockSyntax || cls.ClassStatement.TypeParameterList is not null || !cls.ClassStatement.Identifier.ValueText.Equals(className, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!found)
            {
                found = true;
                var parts = cls.Ancestors().OfType<NamespaceBlockSyntax>().Reverse().Select(n => n.NamespaceStatement.Name.ToString()).ToList();
                ns = parts.Count == 0 ? null : string.Join(".", parts);
            }
            else
            {
                continue;
            }

            foreach (var member in cls.Members)
            {
                switch (member)
                {
                    case FieldDeclarationSyntax f:
                        foreach (var d in f.Declarators)
                        {
                            foreach (var n in d.Names)
                            {
                                members.Add(n.Identifier.ValueText);
                            }
                        }

                        break;
                    case PropertyStatementSyntax p:
                        members.Add(p.Identifier.ValueText);
                        break;
                    case PropertyBlockSyntax pb:
                        members.Add(pb.PropertyStatement.Identifier.ValueText);
                        break;
                    case EventStatementSyntax e:
                        members.Add(e.Identifier.ValueText);
                        break;
                    case EventBlockSyntax eb:
                        members.Add(eb.EventStatement.Identifier.ValueText);
                        break;
                }
            }
        }

        return members;
    }
}
