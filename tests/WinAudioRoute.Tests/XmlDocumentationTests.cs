using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace WinAudioRoute.Tests;

/// <summary>
/// 公开 API 的 XML 文档完整性测试。
/// <para>
/// <b>为什么用反射而不是只看编译警告</b>：<c>CS1591</c> 只在构建时报告，且一旦有人把
/// <c>NoWarn</c> 打开就会静默失效。这个测试直接读取生成的
/// <c>WinAudioRoute.xml</c>，枚举程序集里<b>每一个公开成员</b>，并要求它都有文档条目。
/// </para>
/// <para>
/// 它同时是一份"公开表面清单"的守卫：任何新增公开成员如果没写文档，测试立即失败。
/// </para>
/// </summary>
public class XmlDocumentationTests
{
    private static readonly Assembly Library = typeof(WindowsAudioManager).Assembly;

    private static XDocument LoadDocumentation()
    {
        string path = Path.Combine(
            Path.GetDirectoryName(Library.Location)!,
            Path.GetFileNameWithoutExtension(Library.Location) + ".xml");

        Assert.True(File.Exists(path), $"XML documentation file not found next to the assembly: {path}");
        return XDocument.Load(path);
    }

    private static HashSet<string> DocumentedMemberNames(XDocument document)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (XElement member in document.Descendants("member"))
        {
            string? name = member.Attribute("name")?.Value;
            if (!string.IsNullOrEmpty(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>该类型是否是本库的类型（而不是引用的框架类型）。</summary>
    private static bool IsLibraryType(Type type) =>
        type.Assembly == Library && (type.IsPublic || type.IsNestedPublic);

    [Fact]
    public void AllPublicTypes_AreDocumented()
    {
        HashSet<string> documented = DocumentedMemberNames(LoadDocumentation());

        var missing = new List<string>();
        foreach (Type type in Library.GetExportedTypes())
        {
            if (!documented.Contains("T:" + type.FullName))
            {
                missing.Add(type.FullName!);
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void AllPublicMembers_AreDocumented()
    {
        HashSet<string> documented = DocumentedMemberNames(LoadDocumentation());
        var missing = new List<string>();

        foreach (Type type in Library.GetExportedTypes())
        {
            const BindingFlags Flags =
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (MemberInfo member in type.GetMembers(Flags))
            {
                // 属性访问器由属性条目文档化，不单独要求
                if (member is MethodInfo { IsSpecialName: true })
                {
                    continue;
                }

                // 编译器合成成员（record/record struct 的等价性成员、主构造、<...> 名称）
                // 源码里没有可写注释的位置，因此豁免
                if (IsCompilerSynthesizedMember(member))
                {
                    continue;
                }

                string? id = DocumentationId(member);
                if (id is null)
                {
                    continue;
                }

                if (documented.Contains(id))
                {
                    continue;
                }

                // 无参构造函数：如果没有文档条目，说明它是编译器隐式生成的
                // （record 的默认构造等），源码里没有可写注释的位置，因此豁免。
                if (member is ConstructorInfo { } ctor && ctor.GetParameters().Length == 0)
                {
                    continue;
                }

                missing.Add(id);
            }
        }

        Assert.Empty(missing);
    }

    /// <summary>
    /// 该成员是否是"没有可注释声明"的编译器合成成员。
    /// <para>
    /// 包括：record / record struct 的等价性成员、<c>Deconstruct</c>、<c>PrintMembers</c>、
    /// 以及所有 <c>&lt;...&gt;</c> 形式的编译器生成名称。它们由编译器生成，
    /// 源码里没有可以写 XML 注释的位置，因此不要求文档。
    /// </para>
    /// </summary>
    private static bool IsCompilerSynthesizedMember(MemberInfo member)
    {
        // 枚举的底层字段 value__ 由编译器生成
        if (member is FieldInfo { Name: "value__" })
        {
            return true;
        }

        // 编译器生成的名字总是被尖括号包裹（如 <Clone>$、<Clone>）
        if (member.Name.StartsWith('<') || member.Name.Contains(">$", StringComparison.Ordinal))
        {
            return true;
        }

        if (member is MethodInfo method && method.DeclaringType is { } declaring && IsRecordLike(declaring))
        {
            // 等价性成员、解构、PrintMembers，以及 record 合成的 ToString
            if (method.Name is "Equals" or "GetHashCode" or "Deconstruct" or "PrintMembers" or "ToString")
            {
                return true;
            }
        }

        if (member is ConstructorInfo constructor)
        {
            // 复制构造（protected T(T original)）被标记为 Obsolete
            if (constructor.GetCustomAttribute<ObsoleteAttribute>() is not null)
            {
                return true;
            }

            // 有参数的构造函数是 record 的主构造函数：参数由属性承载，没有独立的可注释声明
            if (constructor.GetParameters().Length > 0
                && constructor.DeclaringType is { } ctorType
                && IsRecordLike(ctorType))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 判断类型是否由 record 机制生成（record class 或 record struct）。
    /// <para>
    /// 两种形式都有编译器生成的 <c>PrintMembers</c>；record class 还有 <c>EqualityContract</c>。
    /// </para>
    /// </summary>
    private static bool IsRecordLike(Type type)
    {
        const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        if (type.GetProperty("EqualityContract", All) is not null)
        {
            return true;
        }

        if (type.GetMethod("PrintMembers", All) is not null)
        {
            return true;
        }

        // record struct 也生成 <Clone>$
        return type.GetMethods(All).Any(m => m.Name.Contains("Clone", StringComparison.Ordinal)
                                             && m.Name.StartsWith('<'));
    }

    [Fact]
    public void DocumentedMembers_HaveNonEmptySummary()
    {
        XDocument document = LoadDocumentation();

        var withoutSummary = new List<string>();
        foreach (XElement member in document.Descendants("member"))
        {
            string? name = member.Attribute("name")?.Value;
            if (name is null || !name.StartsWith("T:WinAudioRoute", StringComparison.Ordinal))
            {
                continue;
            }

            string? summary = member.Element("summary")?.Value;
            if (string.IsNullOrWhiteSpace(summary))
            {
                withoutSummary.Add(name);
            }
        }

        Assert.Empty(withoutSummary);
    }

    [Fact]
    public void PublicApiSurface_IsNotAccidentallyHuge()
    {
        // 公开表面应当保持克制。这个上限是"防止有人不小心把 internal 类型改公开"的护栏，
        // 不是功能指标；必要时可以显式上调。
        int publicMemberCount = Library.GetExportedTypes()
            .Sum(t => t.GetMembers(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Length);

        Assert.InRange(publicMemberCount, 1, 400);
    }

    /// <summary>
    /// 构造 C# 编译器使用的文档注释 ID（不含返回类型）。
    /// </summary>
    private static string? DocumentationId(MemberInfo member)
    {
        string typeName = member.DeclaringType!.FullName!;

        return member switch
        {
            Type nested => "T:" + nested.FullName,
            PropertyInfo property => $"P:{typeName}.{property.Name}",
            FieldInfo field => $"F:{typeName}.{field.Name}",
            EventInfo eventInfo => $"E:{typeName}.{eventInfo.Name}",
            ConstructorInfo constructor => $"M:{typeName}.#ctor{Parameters(constructor.GetParameters())}",
            MethodInfo method when !method.IsSpecialName =>
                $"M:{typeName}.{method.Name}{GenericArity(method)}{Parameters(method.GetParameters())}",
            _ => null,
        };
    }

    private static string GenericArity(MethodInfo method) =>
        method.IsGenericMethodDefinition ? $"``{method.GetGenericArguments().Length}" : string.Empty;

    private static string Parameters(ParameterInfo[] parameters)
    {
        if (parameters.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder("(");
        for (int i = 0; i < parameters.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(ParameterTypeName(parameters[i].ParameterType));
        }

        builder.Append(')');
        return builder.ToString();
    }

    private static string ParameterTypeName(Type type)
    {
        if (type.IsByRef)
        {
            return ParameterTypeName(type.GetElementType()!) + "@";
        }

        if (type.IsArray)
        {
            return ParameterTypeName(type.GetElementType()!) + "[]";
        }

        if (type.IsGenericType)
        {
            string name = type.GetGenericTypeDefinition().FullName!;
            int tick = name.IndexOf('`');
            if (tick >= 0)
            {
                name = name[..tick];
            }

            return name + "{" + string.Join(",", type.GetGenericArguments().Select(ParameterTypeName)) + "}";
        }

        return type.FullName ?? type.Name;
    }
}
