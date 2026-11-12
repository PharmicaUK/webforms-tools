using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace WebForms.Formatting;

/// <summary>
/// The syntactic half of Visual Studio's VB case correction: keywords (including preprocessor keywords such as
/// <c>#Region</c>) get their canonical spelling, and the implicit <c>value</c> parameter of a property setter
/// gets its declared spelling (<c>Value</c> unless the setter declares its own parameter). Identifiers are left
/// alone. Every change keeps the token's length.
/// </summary>
internal static class KeywordCasing
{
    public static SourceText Apply(SourceText text, SyntaxNode root, TextSpan span)
    {
        var changes = new List<TextChange>();
        foreach (var token in root.DescendantTokens(span, descendIntoTrivia: true))
        {
            if (!token.Span.IntersectsWith(span) || token.IsMissing)
            {
                continue;
            }

            var canonical = Canonical(token);
            if (canonical is not null && canonical != token.Text && canonical.Length == token.Text.Length)
            {
                changes.Add(new TextChange(token.Span, canonical));
            }
        }

        return changes.Count == 0 ? text : text.WithChanges(changes);
    }

    private static string? Canonical(SyntaxToken token)
    {
        var kind = token.Kind();
        if (SyntaxFacts.IsKeywordKind(kind))
        {
            var canonical = SyntaxFacts.GetText(kind);
            return string.Equals(canonical, token.Text, StringComparison.OrdinalIgnoreCase) ? canonical : null;
        }

        if (kind == SyntaxKind.IdentifierToken && token.Parent is IdentifierNameSyntax name &&
            string.Equals(token.ValueText, "value", StringComparison.OrdinalIgnoreCase) &&
            !IsMemberName(name))
        {
            return SetterParameterName(name);
        }

        return null;
    }

    private static bool IsMemberName(IdentifierNameSyntax name) => name.Parent switch
    {
        MemberAccessExpressionSyntax m => m.Name == name,
        QualifiedNameSyntax q => q.Right == name,
        NamedFieldInitializerSyntax f => f.Name == name,
        NameColonEqualsSyntax => true,
        _ => false,
    };

    /// <summary>The spelling of the parameter <c>value</c> refers to, or null when it is not in a setter.</summary>
    private static string? SetterParameterName(SyntaxNode node)
    {
        var accessor = node.FirstAncestorOrSelf<AccessorBlockSyntax>(a => a.IsKind(SyntaxKind.SetAccessorBlock));
        if (accessor is null)
        {
            return null;
        }

        var parameters = accessor.AccessorStatement.ParameterList?.Parameters;
        if (parameters is null || parameters.Value.Count == 0)
        {
            return "Value";
        }

        var declared = parameters.Value[0].Identifier.Identifier;
        return string.Equals(declared.ValueText, "value", StringComparison.OrdinalIgnoreCase) ? declared.ValueText : null;
    }
}
