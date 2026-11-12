using System.Collections.Concurrent;
using System.Reflection;

namespace WebForms.Designer;

/// <summary>A public instance property of a control (or inner object) type, as markup sees it.</summary>
/// <param name="DeclaringType">Full name of the type that declares it (for documentation lookups).</param>
/// <param name="Settable">Has a public setter somewhere in the hierarchy: can be set from an attribute.</param>
/// <param name="IsContent">
/// <c>[DesignerSerializationVisibility(Content)]</c> on a read-only object (<c>Font</c>, <c>HeaderStyle</c>): its own
/// properties are set with the hyphen syntax (<c>Font-Bold</c>, <c>HeaderStyle-CssClass</c>).
/// </param>
/// <param name="IsInner">Persisted as an inner tag (<c>[PersistenceMode(InnerProperty)]</c> or a template).</param>
public sealed record ControlProperty(
    string Name,
    string DeclaringType,
    string DeclaringAssembly,
    ControlType? Type,
    string TypeName,
    bool Settable,
    bool IsTemplate,
    bool IsCollection,
    bool IsContent,
    bool IsInner,
    string? DefaultValue);

/// <summary>A public instance event; <paramref name="ArgsTypes"/> is the event-args type and its base types (full names), most derived first.</summary>
public sealed record ControlEvent(string Name, string DeclaringType, string DeclaringAssembly, string HandlerTypeName, IReadOnlyList<string> ArgsTypes);

public sealed partial class ControlType
{
    private IReadOnlyList<ControlProperty>? _properties;
    private IReadOnlyList<ControlEvent>? _events;
    private IReadOnlyList<string>? _enumValues;

    public bool IsAbstract => Type.IsAbstract;

    /// <summary>This type is <paramref name="other"/> or derives from it.</summary>
    public bool IsSubclassOf(ControlType other) => IsSubclassOf(other.Type);

    public bool IsEnum
    {
        get
        {
            try
            {
                return Type.IsEnum;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
        }
    }

    /// <summary>Simple name of the assembly that defines the type.</summary>
    public string AssemblyName => Type.Assembly.GetName().Name ?? "";

    /// <summary>Public static field names of an enum type (empty for other types).</summary>
    public IReadOnlyList<string> EnumValues => _enumValues ??= ComputeEnumValues();

    /// <summary>
    /// Public instance properties without indexers, most derived declaration first (an override or a Shadows
    /// declaration hides the base one), excluding <c>[Browsable(false)]</c> and <c>[Obsolete]</c> ones.
    /// </summary>
    public IReadOnlyList<ControlProperty> Properties => _properties ??= ComputeProperties();

    /// <summary>Public instance events, most derived first, excluding <c>[Browsable(false)]</c> ones.</summary>
    public IReadOnlyList<ControlEvent> Events => _events ??= ComputeEvents();

    public ControlProperty? GetProperty(string name) => Properties.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A property, or a sub-property in the hyphen syntax (<c>Font-Bold</c>, <c>HeaderStyle-Font-Bold</c>), or null.</summary>
    public ControlProperty? ResolvePropertyPath(string name)
    {
        var parts = name.Split('-');
        var owner = this;
        ControlProperty? p = null;
        for (int i = 0; i < parts.Length; i++)
        {
            p = owner?.GetProperty(parts[i]);
            if (p is null || (i < parts.Length - 1 && !p.IsContent))
            {
                return null;
            }

            owner = p.Type;
        }

        return p;
    }

    public ControlEvent? GetEvent(string name) => Events.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private List<string> ComputeEnumValues()
    {
        if (!IsEnum)
        {
            return [];
        }

        try
        {
            return [.. Type.GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.Name)];
        }
        catch (FileNotFoundException)
        {
            return [];
        }
    }

    private List<ControlProperty> ComputeProperties()
    {
        // Per name: the first (most derived) declaration wins for type and attributes; settable if any declaration has a public setter.
        var byName = new Dictionary<string, (PropertyInfo Info, bool Settable, bool? Browsable, bool Obsolete)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        for (var t = Type; t is not null; t = SafeBase(t))
        {
            PropertyInfo[] props;
            try
            {
                props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            foreach (var p in props)
            {
                bool settable;
                bool? browsable;
                bool obsolete;
                try
                {
                    if (p.GetIndexParameters().Length > 0)
                    {
                        continue;
                    }

                    settable = p.GetSetMethod() is not null;
                    var attrs = SafeAttrs(p);
                    browsable = attrs.FirstOrDefault(a => a.AttributeType.FullName == "System.ComponentModel.BrowsableAttribute") is { ConstructorArguments: [{ Value: bool b }] } ? b : null;
                    obsolete = attrs.Any(a => a.AttributeType.FullName == "System.ObsoleteAttribute");
                }
                catch (FileNotFoundException)
                {
                    continue;
                }

                if (byName.TryGetValue(p.Name, out var existing))
                {
                    byName[p.Name] = (existing.Info, existing.Settable || settable, existing.Browsable ?? browsable, existing.Obsolete);
                }
                else
                {
                    byName[p.Name] = (p, settable, browsable, obsolete);
                    order.Add(p.Name);
                }
            }
        }

        var result = new List<ControlProperty>();
        foreach (var name in order)
        {
            var (info, settable, browsable, obsolete) = byName[name];
            // Templates are [Browsable(false)] (Visual Studio edits them in template mode) but are written in markup.
            if (!obsolete && Describe(info, settable) is { } prop && (browsable != false || prop.IsInner))
            {
                result.Add(prop);
            }
        }

        return result;
    }

    private ControlProperty? Describe(PropertyInfo info, bool settable)
    {
        Type pt;
        try
        {
            pt = info.PropertyType;
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        var attrs = SafeAttrs(info);
        bool template = pt.FullName == "System.Web.UI.ITemplate";
        bool collection = !template && pt.FullName != "System.String" && TypeSystem.IsCollection(pt);
        int persistence = attrs.FirstOrDefault(a => a.AttributeType.FullName == "System.Web.UI.PersistenceModeAttribute") is { ConstructorArguments: [{ Value: { } v }] }
            ? Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)
            : 0;
        bool content = attrs.FirstOrDefault(a => a.AttributeType.FullName == "System.ComponentModel.DesignerSerializationVisibilityAttribute") is { ConstructorArguments: [{ Value: { } dv }] } &&
                       Convert.ToInt32(dv, System.Globalization.CultureInfo.InvariantCulture) == 2;
        string? defaultValue = attrs.FirstOrDefault(a => a.AttributeType.FullName == "System.ComponentModel.DefaultValueAttribute") is { } d && d.ConstructorArguments.Count > 0
            ? FormatDefault(d.ConstructorArguments[^1])
            : null;

        return new ControlProperty(
            info.Name,
            (info.DeclaringType?.FullName ?? Type.FullName ?? "").Replace('+', '.'),
            (info.DeclaringType ?? Type).Assembly.GetName().Name ?? "",
            template ? null : _system.Wrap(pt),
            TypeSystem.DisplayName(pt),
            settable,
            template,
            collection,
            content && !settable && !collection && !template,
            template || persistence is 1 or 2 or 3,
            defaultValue);
    }

    private static string? FormatDefault(CustomAttributeTypedArgument arg)
    {
        if (arg.Value is null)
        {
            return null;
        }

        if (arg.ArgumentType.IsEnum)
        {
            try
            {
                var name = arg.ArgumentType.GetFields(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(f => Equals(f.GetRawConstantValue(), arg.Value))?.Name;
                return name ?? arg.Value.ToString();
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
            {
                return arg.Value.ToString();
            }
        }

        return arg.Value switch
        {
            bool b => b ? "True" : "False",
            string s => s.Length == 0 ? "\"\"" : s,
            Type t => t.Name,
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            var o => o.ToString(),
        };
    }

    private List<ControlEvent> ComputeEvents()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ControlEvent>();
        for (var t = Type; t is not null; t = SafeBase(t))
        {
            EventInfo[] events;
            try
            {
                events = t.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            foreach (var e in events)
            {
                if (!seen.Add(e.Name))
                {
                    continue;
                }

                if (SafeAttrs(e).FirstOrDefault(a => a.AttributeType.FullName == "System.ComponentModel.BrowsableAttribute") is { ConstructorArguments: [{ Value: false }] })
                {
                    continue;
                }

                string handler = "";
                var args = new List<string>();
                try
                {
                    var handlerType = e.EventHandlerType;
                    handler = handlerType is null ? "" : TypeSystem.DisplayName(handlerType);
                    if (handlerType?.GetMethod("Invoke")?.GetParameters() is [_, var second])
                    {
                        for (var a = second.ParameterType; a is not null && a.FullName != "System.Object"; a = SafeBase(a))
                        {
                            args.Add((a.FullName ?? a.Name).Replace('+', '.'));
                        }
                    }
                }
                catch (FileNotFoundException)
                {
                    // Missing dependency: the event is still listed, without a signature.
                }

                result.Add(new ControlEvent(e.Name, (t.FullName ?? "").Replace('+', '.'), t.Assembly.GetName().Name ?? "", handler, args));
            }
        }

        return result;
    }
}

public sealed partial class TypeSystem
{
    private readonly ConcurrentDictionary<(string Assembly, string Namespace), IReadOnlyList<ControlType>> _namespaceTypes = new();

    /// <summary>
    /// Public, non-abstract, non-generic, non-nested types of a namespace in an assembly (case-insensitive
    /// namespace, like the ASP.NET parser), in name order. Empty if the assembly is not found.
    /// </summary>
    public IReadOnlyList<ControlType> TypesInNamespace(string assemblyName, string ns) =>
        _namespaceTypes.GetOrAdd((assemblyName.Split(',')[0].Trim(), ns), key =>
        {
            var index = _typeIndex.GetOrAdd(key.Assembly, BuildIndex);
            if (index is null)
            {
                return [];
            }

            var list = new List<ControlType>();
            foreach (var (fullName, type) in index)
            {
                int dot = fullName.LastIndexOf('.');
                if (dot < 0 || !fullName.AsSpan(0, dot).Equals(key.Namespace, StringComparison.OrdinalIgnoreCase) ||
                    type.IsNested || type.IsGenericTypeDefinition || type.IsAbstract || type.IsInterface)
                {
                    continue;
                }

                list.Add(Wrap(type));
            }

            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        });

    /// <summary>The file an assembly (simple name) is read from, or null.</summary>
    public string? GetAssemblyPath(string simpleName) => _assemblyPaths.GetValueOrDefault(simpleName.Split(',')[0].Trim());

    /// <summary>A type name as VB shows it: <c>String</c>, <c>Boolean</c>, <c>Unit</c>, <c>ButtonType</c>.</summary>
    internal static string DisplayName(Type t)
    {
        try
        {
            return t.FullName switch
            {
                "System.String" => "String",
                "System.Boolean" => "Boolean",
                "System.Int32" => "Integer",
                "System.Int16" => "Short",
                "System.Int64" => "Long",
                "System.Double" => "Double",
                "System.Single" => "Single",
                "System.Decimal" => "Decimal",
                "System.Object" => "Object",
                "System.Char" => "Char",
                "System.Byte" => "Byte",
                "System.DateTime" => "Date",
                _ => t.Name.Split('`')[0],
            };
        }
        catch (FileNotFoundException)
        {
            return "?";
        }
    }
}
