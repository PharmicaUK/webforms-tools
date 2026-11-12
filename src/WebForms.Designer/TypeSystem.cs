using System.Collections.Concurrent;
using System.Reflection;

namespace WebForms.Designer;

/// <summary>What kind of inner property an element names.</summary>
public enum InnerPropertyKind
{
    Unknown,
    Template,
    Collection,
    Complex,
}

/// <param name="IgnoreUnknownContent">
/// The property has <c>[IgnoreUnknownContent]</c> (e.g. <c>HtmlTable.Rows</c>): child tags that are not of the
/// collection's item type are dropped as literal content instead of being a parse error.
/// </param>
public sealed record InnerProperty(
    string Name,
    InnerPropertyKind Kind,
    bool SingleInstanceTemplate,
    ControlType? PropertyType,
    ControlType? CollectionItemType,
    bool IgnoreUnknownContent = false)
{
    /// <summary>For a template: the <c>[TemplateContainer]</c> type (what <c>Container</c> is in its data-binding expressions), as a full name.</summary>
    public string? TemplateContainer { get; init; }
}

/// <summary>Metadata about a control (or inner object) type, read from reference assemblies.</summary>
public sealed partial class ControlType
{
    private readonly TypeSystem _system;
    private readonly ConcurrentDictionary<string, InnerProperty?> _props = new(StringComparer.OrdinalIgnoreCase);
    private (bool AsProperties, string? DefaultProperty)? _parseChildren;
    private bool? _isControl;

    internal ControlType(TypeSystem system, Type type)
    {
        _system = system;
        Type = type;
    }

    internal Type Type { get; }

    /// <summary>Full CLR name with nested types joined by '.' (as written in VB source).</summary>
    public string FullName => (Type.FullName ?? Type.Name).Replace('+', '.');

    public string Name => Type.Name;

    public bool IsControl => _isControl ??= _system.ControlBase is { } c && IsSubclassOf(c.Type);

    public bool IsSubclassOf(Type baseType)
    {
        for (var t = Type; t is not null; t = SafeBase(t))
        {
            if (t.FullName == baseType.FullName)
            {
                return true;
            }
        }

        return false;
    }

    public bool IsSubclassOf(string fullName)
    {
        for (var t = Type; t is not null; t = SafeBase(t))
        {
            if (t.FullName == fullName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the type (or a base type) declares a public instance event with this name (case-insensitive, like the ASP.NET parser).</summary>
    public bool HasEvent(string name)
    {
        for (var t = Type; t is not null; t = SafeBase(t))
        {
            try
            {
                foreach (var e in t.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (FileNotFoundException)
            {
                // A dependency assembly is missing; keep looking in what can be read.
            }
        }

        return false;
    }

    /// <summary>The effective ParseChildrenAttribute (inherited). Controls without one parse children as controls; non-controls as properties.</summary>
    public (bool AsProperties, string? DefaultProperty) ParseChildren => _parseChildren ??= ComputeParseChildren();

    /// <summary>Name of the ControlBuilderAttribute type (inherited), if any.</summary>
    public string? ControlBuilderTypeName
    {
        get
        {
            for (var t = Type; t is not null; t = SafeBase(t))
            {
                foreach (var a in SafeAttrs(t))
                {
                    if (a.AttributeType.FullName == "System.Web.UI.ControlBuilderAttribute" && a.ConstructorArguments.Count == 1)
                    {
                        return (a.ConstructorArguments[0].Value as Type)?.FullName;
                    }
                }
            }

            return null;
        }
    }

    private (bool, string?) ComputeParseChildren()
    {
        for (var t = Type; t is not null; t = SafeBase(t))
        {
            foreach (var a in SafeAttrs(t))
            {
                if (a.AttributeType.FullName != "System.Web.UI.ParseChildrenAttribute")
                {
                    continue;
                }

                bool asProps = false;
                string? defaultProp = null;
                var args = a.ConstructorArguments;
                if (args.Count >= 1 && args[0].Value is bool b)
                {
                    asProps = b;
                }
                else if (args.Count == 1 && args[0].Value is Type)
                {
                    // ParseChildren(typeof(T)): children are controls of type T.
                    asProps = false;
                }

                if (args.Count >= 2 && args[1].Value is string s)
                {
                    defaultProp = s;
                }

                foreach (var na in a.NamedArguments)
                {
                    if (na.MemberName == "ChildrenAsProperties" && na.TypedValue.Value is bool nb)
                    {
                        asProps = nb;
                    }
                    else if (na.MemberName == "DefaultProperty" && na.TypedValue.Value is string ns)
                    {
                        defaultProp = ns;
                    }
                }

                return (asProps, string.IsNullOrEmpty(defaultProp) ? null : defaultProp);
            }
        }

        return (!IsControl, null);
    }

    public InnerProperty? GetInnerProperty(string name) => _props.GetOrAdd(name, ComputeInnerProperty);

    private InnerProperty? ComputeInnerProperty(string name)
    {
        PropertyInfo? prop = null;
        for (var t = Type; t is not null && prop is null; t = SafeBase(t))
        {
            try
            {
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.GetIndexParameters().Length == 0)
                    {
                        prop = p;
                        break;
                    }
                }
            }
            catch (FileNotFoundException)
            {
                // A dependency assembly is missing; treat as unknown.
            }
        }

        if (prop is null)
        {
            return null;
        }

        Type pt;
        try
        {
            pt = prop.PropertyType;
        }
        catch (FileNotFoundException)
        {
            return new InnerProperty(prop.Name, InnerPropertyKind.Unknown, false, null, null);
        }

        if (pt.FullName == "System.Web.UI.ITemplate")
        {
            bool single = false;
            string? container = null;
            foreach (var a in SafeAttrs(prop))
            {
                if (a.AttributeType.FullName == "System.Web.UI.TemplateContainerAttribute" &&
                    a.ConstructorArguments.Count >= 1 && a.ConstructorArguments[0].Value is Type containerType)
                {
                    container = (containerType.FullName ?? containerType.Name).Replace('+', '.');
                }


                if (a.AttributeType.FullName == "System.Web.UI.TemplateInstanceAttribute" &&
                    a.ConstructorArguments.Count == 1 &&
                    Convert.ToInt32(a.ConstructorArguments[0].Value) == 1)
                {
                    single = true;
                }
            }

            return new InnerProperty(prop.Name, InnerPropertyKind.Template, single, null, null) { TemplateContainer = container };
        }

        if (TypeSystem.IsCollection(pt))
        {
            bool ignoreUnknown = SafeAttrs(prop).Any(a => a.AttributeType.FullName == "System.Web.UI.IgnoreUnknownContentAttribute");
            return new InnerProperty(prop.Name, InnerPropertyKind.Collection, false, _system.Wrap(pt), _system.GetCollectionItemType(pt), ignoreUnknown);
        }

        return new InnerProperty(prop.Name, InnerPropertyKind.Complex, false, _system.Wrap(pt), null);
    }

    internal static Type? SafeBase(Type t)
    {
        try
        {
            return t.BaseType;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    internal static IList<CustomAttributeData> SafeAttrs(MemberInfo m)
    {
        try
        {
            return m.GetCustomAttributesData();
        }
        catch (FileNotFoundException)
        {
            return [];
        }
    }

    public override string ToString() => FullName;
}

/// <summary>
/// Loads control metadata from .NET Framework reference assemblies and project references using
/// MetadataLoadContext (no code is executed, works from .NET 10).
/// </summary>
public sealed partial class TypeSystem : IDisposable
{
    private readonly MetadataLoadContext _mlc;
    private readonly Dictionary<string, string> _assemblyPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Assembly?> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Type, ControlType> _wrapped = new();
    private readonly ConcurrentDictionary<string, Dictionary<string, Type>?> _typeIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _onProjectAssemblyLoaded;
    private ControlType? _controlBase;

    /// <param name="frameworkReferenceDirectory">The .NET Framework reference assemblies (memory-mapped: they never change).</param>
    /// <param name="extraAssemblies">
    /// Project references (HintPaths, bin). These are read into memory instead of being memory-mapped: a mapped file
    /// is locked on Windows, and a long-running process (the language server) would then break builds that write bin.
    /// </param>
    /// <param name="onProjectAssemblyLoaded">Called with the path of each project reference when it is loaded.</param>
    public TypeSystem(string frameworkReferenceDirectory, IEnumerable<string> extraAssemblies, Action<string>? onProjectAssemblyLoaded = null)
    {
        _onProjectAssemblyLoaded = onProjectAssemblyLoaded;
        FrameworkDirectory = frameworkReferenceDirectory;
        var paths = new List<string>();
        foreach (var dir in new[] { frameworkReferenceDirectory, Path.Combine(frameworkReferenceDirectory, "Facades") })
        {
            if (Directory.Exists(dir))
            {
                paths.AddRange(Directory.EnumerateFiles(dir, "*.dll"));
            }
        }

        foreach (var p in extraAssemblies)
        {
            if (File.Exists(p))
            {
                paths.Add(p);
            }
        }

        foreach (var p in paths)
        {
            _assemblyPaths.TryAdd(Path.GetFileNameWithoutExtension(p), p);
        }

        _mlc = new MetadataLoadContext(new Resolver(this), "mscorlib");
    }

    public string FrameworkDirectory { get; }

    public ControlType? ControlBase => _controlBase ??= FindType("System.Web", "System.Web.UI.Control");

    public bool HasAssembly(string simpleName) => _assemblyPaths.ContainsKey(simpleName);

    /// <summary>Find a type by full name in an assembly given by simple name or display name. Case-insensitive, like ASP.NET.</summary>
    public ControlType? FindType(string assemblyName, string fullName)
    {
        string simple = assemblyName.Split(',')[0].Trim();
        var index = _typeIndex.GetOrAdd(simple, BuildIndex);
        if (index is null)
        {
            return null;
        }

        return index.TryGetValue(fullName, out var t) ? Wrap(t) : null;
    }

    private Dictionary<string, Type>? BuildIndex(string simpleName)
    {
        var asm = Load(simpleName);
        if (asm is null)
        {
            return null;
        }

        var dict = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        Type?[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }

        foreach (var t in types)
        {
            if (t?.FullName is { } fn && (t.IsPublic || t.IsNestedPublic))
            {
                dict.TryAdd(fn.Replace('+', '.'), t);
            }
        }

        return dict;
    }

    private Assembly? Load(string simpleName) => _loaded.GetOrAdd(simpleName, n =>
    {
        if (!_assemblyPaths.ContainsKey(n))
        {
            return null;
        }

        try
        {
            return _mlc.LoadFromAssemblyName(n);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException or IOException)
        {
            return null;
        }
    });

    private Assembly? LoadInto(MetadataLoadContext context, string simpleName)
    {
        if (!_assemblyPaths.TryGetValue(simpleName, out var path))
        {
            return null;
        }

        if (path.StartsWith(FrameworkDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return context.LoadFromAssemblyPath(path);
        }

        var assembly = context.LoadFromByteArray(File.ReadAllBytes(path));
        _onProjectAssemblyLoaded?.Invoke(path);
        return assembly;
    }

    /// <summary>Resolves by simple name, like the ASP.NET build, which binds whatever is in bin.</summary>
    private sealed class Resolver(TypeSystem owner) : MetadataAssemblyResolver
    {
        public override Assembly? Resolve(MetadataLoadContext context, AssemblyName assemblyName) =>
            assemblyName.Name is { } name ? owner.LoadInto(context, name) : null;
    }

    internal ControlType Wrap(Type t) => _wrapped.GetOrAdd(t, x => new ControlType(this, x));

    internal static bool IsCollection(Type t)
    {
        try
        {
            if (t.IsArray)
            {
                return true;
            }

            foreach (var i in t.GetInterfaces())
            {
                if (i.FullName is "System.Collections.ICollection" or "System.Collections.IList")
                {
                    return true;
                }
            }
        }
        catch (FileNotFoundException)
        {
        }

        return false;
    }

    internal ControlType? GetCollectionItemType(Type t)
    {
        try
        {
            if (t.IsArray)
            {
                return t.GetElementType() is { } e ? Wrap(e) : null;
            }

            for (var x = t; x is not null; x = ControlType.SafeBase(x))
            {
                foreach (var p in x.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (p.Name == "Item" && p.GetIndexParameters() is [{ ParameterType.FullName: "System.Int32" }])
                    {
                        return p.PropertyType.FullName == "System.Object" ? null : Wrap(p.PropertyType);
                    }
                }
            }
        }
        catch (FileNotFoundException)
        {
        }

        return null;
    }

    public void Dispose() => _mlc.Dispose();
}
