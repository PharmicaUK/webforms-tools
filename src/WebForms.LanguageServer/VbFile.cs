using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using WebForms.LanguageServer.Protocol;

namespace WebForms.LanguageServer;

/// <summary>A method that handles an event of a WithEvents member: <c>Sub X(...) Handles btnSave.Click</c>.</summary>
public sealed record HandlesClause(string Container, string EventName, TextSpan MethodName);

/// <summary>A Sub of the code-behind class: a handler candidate. <paramref name="ParameterTypes"/> are simple names.</summary>
/// <param name="CallableFromMarkup">
/// Accessible from the page class ASP.NET compiles from the markup: that class inherits from the code-behind class and
/// lives in another assembly, so Private and Friend Subs are not accessible (Protected Friend is).
/// </param>
public sealed record SubInfo(string Name, IReadOnlyList<string> ParameterTypes, string Signature, bool HasHandles, bool CallableFromMarkup);

/// <summary>
/// A VB file parsed with Roslyn, syntax only: no compilation and no references, so lookups are by name within the
/// partial class declarations of one file. Binding the whole project would take seconds and, without every
/// reference, binds wrongly (see "Formatter findings" in CLAUDE.md).
/// </summary>
public sealed class VbFile
{
    private VbFile(string path, SyntaxTree tree)
    {
        Path = path;
        Tree = tree;
    }

    public string Path { get; }
    public SyntaxTree Tree { get; }

    public static VbFile Parse(string path, string text) =>
        new(path, VisualBasicSyntaxTree.ParseText(text, path: path));

    public LspLocation Location(TextSpan span)
    {
        var s = Tree.GetLineSpan(span).Span;
        return new LspLocation(
            DocumentUri.FromPath(Path),
            new LspRange(new LspPosition(s.Start.Line, s.Start.Character), new LspPosition(s.End.Line, s.End.Character)));
    }

    /// <summary>The class blocks named <paramref name="className"/> (case-insensitive, any namespace); all classes if none match.</summary>
    public IReadOnlyList<TypeBlockSyntax> Classes(string? className)
    {
        var all = Tree.GetRoot().DescendantNodes(n => n is CompilationUnitSyntax or NamespaceBlockSyntax or TypeBlockSyntax)
            .OfType<TypeBlockSyntax>()
            .Where(t => t is ClassBlockSyntax or ModuleBlockSyntax or StructureBlockSyntax)
            .ToList();
        if (className is not null)
        {
            var named = all.Where(t => Eq(t.BlockStatement.Identifier.ValueText, className)).ToList();
            if (named.Count > 0)
            {
                return named;
            }
        }

        return all;
    }

    /// <summary>Classes with their namespace blocks, as <c>Ns.Class</c> (relative to the project root namespace, as VB declares them).</summary>
    public IEnumerable<string> QualifiedClassNames() =>
        Classes(null).OfType<ClassBlockSyntax>()
            .Select(c => string.Join('.', c.Ancestors().OfType<NamespaceBlockSyntax>().Reverse().Select(n => n.NamespaceStatement.Name.ToString())
                .Append(c.BlockStatement.Identifier.ValueText)))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The identifier of the class statement(s).</summary>
    public IEnumerable<TextSpan> ClassDeclarations(string className) =>
        Classes(className).Where(t => Eq(t.BlockStatement.Identifier.ValueText, className)).Select(t => t.BlockStatement.Identifier.Span);

    /// <summary>Subs and Functions with this name declared directly in the class.</summary>
    public IEnumerable<TextSpan> Methods(string? className, string name) =>
        Members(className).OfType<MethodBlockSyntax>()
            .Select(m => m.SubOrFunctionStatement.Identifier)
            .Concat(Members(className).OfType<MethodStatementSyntax>().Select(m => m.Identifier)) // MustOverride / Declare-like
            .Where(id => Eq(id.ValueText, name))
            .Select(id => id.Span);

    /// <summary>Names of all Subs (handler candidates), in declaration order.</summary>
    public IEnumerable<string> SubNames(string? className) =>
        Members(className).OfType<MethodBlockSyntax>()
            .Where(m => m.SubOrFunctionStatement.SubOrFunctionKeyword.IsKind(SyntaxKind.SubKeyword))
            .Select(m => m.SubOrFunctionStatement.Identifier.ValueText);

    /// <summary>Subs declared in the class with the simple names of their parameter types (<c>EventArgs</c> for <c>System.EventArgs</c>), in declaration order.</summary>
    public IEnumerable<SubInfo> Subs(string? className) =>
        Members(className).OfType<MethodBlockSyntax>()
            .Where(m => m.SubOrFunctionStatement.SubOrFunctionKeyword.IsKind(SyntaxKind.SubKeyword))
            .Select(m => new SubInfo(
                m.SubOrFunctionStatement.Identifier.ValueText,
                [.. (m.SubOrFunctionStatement.ParameterList?.Parameters ?? default).Select(p => SimpleTypeName(p.AsClause?.Type))],
                m.SubOrFunctionStatement.ToString().Trim(),
                m.SubOrFunctionStatement.HandlesClause is not null,
                IsCallableFromDerivedClass(m.SubOrFunctionStatement.Modifiers)));

    /// <summary>Public properties, fields and events of the class (what a user control exposes to markup), in declaration order.</summary>
    public IEnumerable<(string Name, string Kind, string TypeName)> PublicMembers(string? className)
    {
        foreach (var member in Members(className))
        {
            switch (member)
            {
                case PropertyBlockSyntax pb when IsPublic(pb.PropertyStatement.Modifiers):
                    yield return (pb.PropertyStatement.Identifier.ValueText, "Property", SimpleTypeName((pb.PropertyStatement.AsClause as SimpleAsClauseSyntax)?.Type));
                    break;
                case PropertyStatementSyntax ps when IsPublic(ps.Modifiers):
                    yield return (ps.Identifier.ValueText, "Property", SimpleTypeName((ps.AsClause as SimpleAsClauseSyntax)?.Type));
                    break;
                case FieldDeclarationSyntax f when f.Modifiers.Any(SyntaxKind.PublicKeyword): // fields default to Private
                    foreach (var d in f.Declarators)
                    {
                        foreach (var n in d.Names)
                        {
                            yield return (n.Identifier.ValueText, "Property", SimpleTypeName((d.AsClause as SimpleAsClauseSyntax)?.Type));
                        }
                    }

                    break;
                case EventStatementSyntax e when IsPublic(e.Modifiers):
                    yield return (e.Identifier.ValueText, "Event", SimpleTypeName(e.AsClause?.Type));
                    break;
                case EventBlockSyntax eb when IsPublic(eb.EventStatement.Modifiers):
                    yield return (eb.EventStatement.Identifier.ValueText, "Event", SimpleTypeName(eb.EventStatement.AsClause?.Type));
                    break;
            }
        }
    }

    /// <summary>
    /// Public, Protected, Protected Friend, or no access modifier (methods default to Public): not Private, Friend or
    /// Private Protected, which a derived class in another assembly can't reach.
    /// </summary>
    public static bool IsCallableFromDerivedClass(SyntaxTokenList modifiers) =>
        !modifiers.Any(SyntaxKind.PrivateKeyword) &&
        !(modifiers.Any(SyntaxKind.FriendKeyword) && !modifiers.Any(SyntaxKind.ProtectedKeyword));

    /// <summary>Public, or no access modifier (properties and events default to Public). Friend is not visible: markup compiles into another assembly.</summary>
    private static bool IsPublic(SyntaxTokenList modifiers) =>
        modifiers.Any(SyntaxKind.PublicKeyword) ||
        !(modifiers.Any(SyntaxKind.PrivateKeyword) || modifiers.Any(SyntaxKind.ProtectedKeyword) || modifiers.Any(SyntaxKind.FriendKeyword));

    /// <summary><c>System.EventArgs</c> -> <c>EventArgs</c>; <c>List(Of X)</c> -> <c>List</c>; null -> <c>Object</c> (VB's default).</summary>
    private static string SimpleTypeName(TypeSyntax? type) => type switch
    {
        null => "Object",
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        PredefinedTypeSyntax p => p.Keyword.ValueText,
        _ => type.ToString(),
    };

    /// <summary>Methods with a <c>Handles name.Event</c> clause.</summary>
    public IEnumerable<HandlesClause> Handles(string? className, string container)
    {
        foreach (var m in Members(className).OfType<MethodBlockSyntax>())
        {
            var handles = m.SubOrFunctionStatement.HandlesClause;
            if (handles is null)
            {
                continue;
            }

            foreach (var item in handles.Events)
            {
                if (item.EventContainer is WithEventsEventContainerSyntax wc && Eq(wc.Identifier.ValueText, container))
                {
                    yield return new HandlesClause(wc.Identifier.ValueText, item.EventMember.Identifier.ValueText, m.SubOrFunctionStatement.Identifier.Span);
                }
            }
        }
    }

    /// <summary>A field, property or event declared in the class with this name.</summary>
    public IEnumerable<TextSpan> MemberDeclarations(string? className, string name)
    {
        foreach (var member in Members(className))
        {
            switch (member)
            {
                case FieldDeclarationSyntax field:
                    foreach (var declarator in field.Declarators)
                    {
                        foreach (var n in declarator.Names)
                        {
                            if (Eq(n.Identifier.ValueText, name))
                            {
                                yield return n.Identifier.Span;
                            }
                        }
                    }

                    break;
                case PropertyBlockSyntax pb when Eq(pb.PropertyStatement.Identifier.ValueText, name):
                    yield return pb.PropertyStatement.Identifier.Span;
                    break;
                case PropertyStatementSyntax ps when Eq(ps.Identifier.ValueText, name):
                    yield return ps.Identifier.Span;
                    break;
            }
        }
    }

    /// <summary>
    /// Uses of a member by simple name inside the class: <c>btnSave.Text</c>, <c>Me.btnSave</c>, arguments, … but not
    /// <c>other.btnSave</c> (a member of something else) and not the declaration itself.
    /// </summary>
    public IEnumerable<TextSpan> Uses(string? className, string name)
    {
        foreach (var type in Classes(className))
        {
            foreach (var id in type.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (!Eq(id.Identifier.ValueText, name))
                {
                    continue;
                }

                if (id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id &&
                    ma.Expression is not (MeExpressionSyntax or MyClassExpressionSyntax or MyBaseExpressionSyntax))
                {
                    continue;
                }

                if (id.Parent is QualifiedNameSyntax qn && qn.Right == id)
                {
                    continue;
                }

                yield return id.Identifier.Span;
            }
        }
    }

    /// <summary>String literals naming a control in <c>FindControl("id")</c> calls (controls inside templates have no field).</summary>
    public IEnumerable<TextSpan> FindControlLiterals(string? className, string id)
    {
        foreach (var type in Classes(className))
        {
            foreach (var invocation in type.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string? method = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
                    IdentifierNameSyntax n => n.Identifier.ValueText,
                    _ => null,
                };
                if (method is null || !Eq(method, "FindControl") || invocation.ArgumentList is null)
                {
                    continue;
                }

                foreach (var arg in invocation.ArgumentList.Arguments)
                {
                    if (arg.GetExpression() is LiteralExpressionSyntax { Token.Value: string s } lit && Eq(s, id))
                    {
                        yield return lit.Span;
                    }
                }
            }
        }
    }

    /// <summary>
    /// For each Sub or Function named <paramref name="name"/> declared in the class: whether the page class compiled
    /// from the markup (a derived class in another assembly) can call it.
    /// </summary>
    public IReadOnlyList<bool> MethodAccess(string className, string name) =>
        [.. Members(className).OfType<MethodBlockSyntax>()
            .Select(m => m.SubOrFunctionStatement)
            .Where(s => Eq(s.Identifier.ValueText, name))
            .Select(s => IsCallableFromDerivedClass(s.Modifiers))];

    /// <summary>The <c>Inherits</c> types of the class's blocks in this file, as written; null when the class isn't declared here.</summary>
    public IReadOnlyList<string>? BaseTypes(string className)
    {
        var blocks = Classes(className).Where(t => Eq(t.BlockStatement.Identifier.ValueText, className)).ToList();
        return blocks.Count == 0 ? null : [.. blocks.SelectMany(b => b.Inherits).SelectMany(i => i.Types).Select(t => t.ToString())];
    }

    private IEnumerable<StatementSyntax> Members(string? className) => Classes(className).SelectMany(t => t.Members);

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
