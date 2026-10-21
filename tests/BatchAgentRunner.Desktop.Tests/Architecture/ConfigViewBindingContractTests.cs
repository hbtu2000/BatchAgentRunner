using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.Architecture;

/// <summary>
/// P-04 视图↔VM 绑定契约守卫（R-4 防回归）。
/// <para>
/// Desktop 使用反射绑定（<c>AvaloniaUseCompiledBindingsByDefault=false</c>，见 Desktop.csproj 注释）：
/// 绑定路径写错不会产生编译错误，只会在运行时静默失效（控件空白 / 命令无反应），
/// 这正是 P-04 分解 <c>ConfigViewModel</c> 时最大的行为破坏向量。本守卫静态解析
/// <c>ConfigView.axaml</c> / <c>ConfigHubView.axaml</c> 的全部 <c>{Binding 路径}</c>，
/// 按 DataContext 作用域（显式 <c>DataContext</c> 属性、<c>DataTemplate</c> 的
/// <c>DataType</c>/<c>x:DataType</c>、无 DataType 模板经 <c>ItemsSource</c> 推断元素类型、
/// <c>$parent[类型].DataContext</c> 祖先查找）逐一校验路径可解析，把「UI 行为等价」
/// 从人工 review 变为机器断言。
/// </para>
/// <para>
/// 覆盖范围说明：只校验「路径存在性」这一最易随 VM 分解漂移的维度；转换器 / 命令参数 /
/// 可见性语义由各视图契约测试（<c>ConfigViewTests</c> 等）与 VM 白箱测试守护。
/// </para>
/// </summary>
public class ConfigViewBindingContractTests
{
    /// <summary>XAML 的 x: 命名空间（x:DataType 等特性）。</summary>
    private static readonly XName XDataType = XName.Get("DataType", "http://schemas.microsoft.com/winfx/2006/xaml");

    /// <summary>视图文件里的 XML 前缀 → CLR 命名空间（与两个 axaml 文件头的 xmlns 声明一致）。</summary>
    private static readonly Dictionary<string, string> PrefixToNamespace = new()
    {
        ["vm"] = "BatchAgentRunner.Desktop.ViewModels",
        ["models"] = "BatchAgentRunner.Desktop.Models",
        ["domain"] = "BatchAgentRunner.Core.Domain",
    };

    [Fact]
    public void ConfigView与ConfigHubView_全部Binding路径可解析()
    {
        var repoRoot = LocateRepoRoot();
        var errors = new List<string>();

        Check(repoRoot, "src/BatchAgentRunner.Desktop/Views/ConfigView.axaml",
            typeof(BatchAgentRunner.Desktop.ViewModels.ConfigViewModel), errors);
        Check(repoRoot, "src/BatchAgentRunner.Desktop/Views/ConfigHubView.axaml",
            typeof(BatchAgentRunner.Desktop.ViewModels.ConfigHubViewModel), errors);

        Assert.True(errors.Count == 0,
            $"以下 Binding 路径无法解析（VM 分解后视图绑定面漂移？）：\n{string.Join("\n", errors)}");
    }

    private static void Check(string repoRoot, string relativePath, Type rootDataContext, List<string> errors)
    {
        var path = Path.Combine(repoRoot, relativePath);
        Assert.True(File.Exists(path), $"未找到视图源文件：{path}（守卫测试须在仓库检出内运行）");

        var doc = XDocument.Load(path);
        var scopes = new Dictionary<XElement, Type>();
        Walk(doc.Root!, rootDataContext, templateItemType: null, scopes, errors, relativePath);
    }

    /// <summary>
    /// 深度优先遍历 XAML 元素树，维护每个元素的 DataContext 作用域类型：
    /// 显式 <c>DataContext="{Binding 路径}"</c> 覆盖 → 处于 DataTemplate 内则为模板元素类型 →
    /// 否则继承父级作用域。元素自身属性里的 Binding 路径按该作用域校验；
    /// <c>$parent[类型]</c> 路径改按该祖先元素的作用域校验。
    /// </summary>
    private static void Walk(
        XElement element,
        Type inheritedScope,
        Type? templateItemType,
        Dictionary<XElement, Type> scopes,
        List<string> errors,
        string file)
    {
        // 1. 显式 DataContext 覆盖（相对当前作用域解析；此时本元素尚未登记作用域，直接用继承值）
        var dataContext = element.Attribute("DataContext");
        if (dataContext is not null)
        {
            var dcPath = ExtractBindingPath(dataContext.Value);
            if (dcPath is not null)
            {
                var resolved = WalkPath(templateItemType ?? inheritedScope, dcPath);
                if (resolved is null)
                {
                    errors.Add($"{file}: DataContext 路径 '{dcPath}' 无法解析（宿主作用域 {Describe(inheritedScope, templateItemType)}）");
                }
                else
                {
                    inheritedScope = resolved;
                    templateItemType = null; // 显式 DataContext 优先于模板元素类型
                }
            }
        }

        // 2. 本元素作用域 = 显式 DataContext 类型 > 模板元素类型 > 继承作用域
        var scope = templateItemType ?? inheritedScope;
        scopes[element] = scope;

        // 3. DataTemplate 的元素类型（供子树与无 DataType 的嵌套模板使用）
        var childItemType = templateItemType;
        if (element.Name.LocalName == "DataTemplate")
        {
            var declared = element.Attribute("DataType")?.Value ?? element.Attribute(XDataType)?.Value;
            if (declared is not null)
            {
                childItemType = ResolveXamlType(declared);
                if (childItemType is null)
                {
                    errors.Add($"{file}: DataTemplate DataType '{declared}' 无法解析为 CLR 类型");
                }
            }
            else
            {
                // 无 DataType：元素类型由宿主 ItemsControl/ComboBox 的 ItemsSource 推断
                var host = element.Parent?.Parent; // *.ItemTemplate → 宿主元素
                var itemsSource = host?.Attribute("ItemsSource");
                if (host is not null && itemsSource is not null)
                {
                    var sourcePath = ExtractBindingPath(itemsSource.Value);
                    if (sourcePath is not null)
                    {
                        var sourceType = EvaluatePath(host, sourcePath, scopes);
                        childItemType = ElementTypeOf(sourceType);
                        if (childItemType is null && sourceType is not null)
                        {
                            errors.Add($"{file}: ItemsSource 路径 '{sourcePath}' 的类型 {sourceType.Name} 不是集合，无法推断模板元素类型");
                        }
                    }
                }
            }
        }

        // 4. 校验本元素全部属性里的 Binding 路径
        foreach (var attribute in element.Attributes())
        {
            if (attribute.Name.LocalName == "DataContext")
            {
                continue; // 已在第 1 步校验
            }

            var bindingPath = ExtractBindingPath(attribute.Value);
            if (bindingPath is null)
            {
                continue;
            }

            var resolved = bindingPath.StartsWith("$parent[")
                ? EvaluatePath(element, bindingPath, scopes)
                : WalkPath(scope, bindingPath);
            if (resolved is null)
            {
                errors.Add($"{file}: <{element.Name.LocalName} {attribute.Name}=\"{bindingPath}\"> 在作用域 {scope.Name} 上无法解析");
            }
        }

        // 5. 递归子树
        foreach (var child in element.Elements())
        {
            Walk(child, scope, childItemType, scopes, errors, file);
        }
    }

    /// <summary>解析 Binding 路径（含 <c>$parent[类型].DataContext.路径</c> 形态）；非绑定值返回 null。</summary>
    private static string? ExtractBindingPath(string value)
    {
        if (!value.StartsWith("{Binding"))
        {
            return null; // {DynamicResource …} / {StaticResource …} / {x:Static …} / 字面量
        }

        var inner = value.Trim('{', '}');
        var rest = inner["Binding".Length..];
        if (rest.Length == 0 || rest[0] != ' ')
        {
            return rest.Length == 0 ? null : null; // {Binding} = 绑定整个 DataContext，无路径可校验
        }

        var path = rest.Split(',')[0].Trim();
        if (path.Length == 0)
        {
            return null;
        }

        // {Binding !路径} 取反绑定（Avalonia 逻辑非）：剥掉前导 ! 后按原路径校验
        var trimmed = path.TrimStart('!');
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>在 <paramref name="element"/> 的上下文里求值 Binding 路径，返回终态类型。</summary>
    private static Type? EvaluatePath(XElement element, string path, Dictionary<XElement, Type> scopes)
    {
        if (!path.StartsWith("$parent["))
        {
            return scopes.TryGetValue(element, out var own) ? WalkPath(own, path) : null;
        }

        // $parent[类型].DataContext.路径 → 找最近同名祖先，用其作用域解析尾部路径
        var close = path.IndexOf(']');
        if (close < 0)
        {
            return null;
        }

        var ancestorName = path["$parent[".Length..close];
        var ancestor = NearestAncestor(element, ancestorName);
        if (ancestor is null || !scopes.TryGetValue(ancestor, out var ancestorScope))
        {
            return null;
        }

        var tail = path[(close + 1)..];
        if (tail.Length == 0)
        {
            return ancestorScope;
        }

        if (!tail.StartsWith(".DataContext"))
        {
            return null; // 本仓未使用 $parent[X].其它形态
        }

        tail = tail[".DataContext".Length..].TrimStart('.');
        return tail.Length == 0 ? ancestorScope : WalkPath(ancestorScope, tail);
    }

    /// <summary>按点分路径逐段取公共实例属性，任一段不存在返回 null。</summary>
    private static Type? WalkPath(Type type, string path)
    {
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var property = type.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
            if (property is null)
            {
                return null;
            }

            type = property.PropertyType;
        }

        return type;
    }

    private static XElement? NearestAncestor(XElement element, string name)
    {
        for (var current = element.Parent; current is not null; current = current.Parent)
        {
            if (current.Name.LocalName == name)
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>取集合类型的元素类型（ObservableCollection&lt;T&gt; / List&lt;T&gt; / T[] → T）。</summary>
    private static Type? ElementTypeOf(Type? type)
    {
        if (type is null || type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        var enumerable = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    /// <summary>把 XAML 的 prefix:TypeName 解析为 CLR 类型（Desktop / Core 两个程序集）。</summary>
    private static Type? ResolveXamlType(string value)
    {
        var separator = value.IndexOf(':');
        if (separator < 0)
        {
            return null;
        }

        var prefix = value[..separator];
        var typeName = value[(separator + 1)..];
        if (!PrefixToNamespace.TryGetValue(prefix, out var namespaceName))
        {
            return null;
        }

        var fullName = $"{namespaceName}.{typeName}";
        return typeof(BatchAgentRunner.Desktop.ViewModels.ConfigViewModel).Assembly.GetType(fullName)
            ?? typeof(BatchAgentRunner.Core.Domain.ModelCandidate).Assembly.GetType(fullName);
    }

    private static string Describe(Type inheritedScope, Type? templateItemType) =>
        templateItemType is null ? inheritedScope.Name : $"{inheritedScope.Name}（模板元素 {templateItemType.Name}）";

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BatchAgentRunner.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
