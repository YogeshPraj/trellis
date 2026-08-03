using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Trellis.Tools.Generator;

/// <summary>
/// Emits a <c>CreateTools()</c> method on every partial class containing [Tool] methods,
/// so tool discovery happens at compile time instead of via assembly scanning.
/// </summary>
[Generator]
public sealed class ToolGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor NotPartial = new(
        id: "TRL001",
        title: "Tool container class must be partial",
        messageFormat: "Class '{0}' contains [Tool] methods but is not declared partial; CreateTools() cannot be generated",
        category: "Trellis.Tools",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateName = new(
        id: "TRL002",
        title: "Duplicate tool name",
        messageFormat: "Tool name '{0}' is used by more than one [Tool] method on '{1}'; only the first is generated. Set distinct names via [Tool(Name = ...)].",
        category: "Trellis.Tools",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedSignature = new(
        id: "TRL003",
        title: "Unsupported tool method signature",
        messageFormat: "[Tool] method '{0}' is skipped: generic methods and ref/out/in parameters are not supported as tools",
        category: "Trellis.Tools",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<ImmutableArray<ToolModel>> tools = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "Trellis.ToolAttribute",
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, _) => Extract(ctx))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!)
            .Collect();

        context.RegisterSourceOutput(tools, static (spc, models) => Emit(spc, models));
    }

    private sealed class ToolModel
    {
        public string Namespace = "";
        public string ClassName = "";
        public bool ClassIsPartial;
        public string MethodName = "";
        public bool IsStatic;
        public string? ToolName;
        public string? Description;
        public string[] ParamTypes = System.Array.Empty<string>();
        public string ReturnType = "";
        public bool ReturnsVoid;
        public bool IsSupported = true;
        public Location? ClassLocation;
        public Location? MethodLocation;
    }

    private static ToolModel? Extract(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not IMethodSymbol method || method.ContainingType is not INamedTypeSymbol type)
        {
            return null;
        }

        string? toolName = null;
        string? description = null;
        foreach (KeyValuePair<string, TypedConstant> arg in ctx.Attributes[0].NamedArguments)
        {
            if (arg.Key == "Name")
            {
                toolName = arg.Value.Value as string;
            }
            else if (arg.Key == "Description")
            {
                description = arg.Value.Value as string;
            }
        }

        bool isPartial = type.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<TypeDeclarationSyntax>()
            .Any(t => t.Modifiers.Any(SyntaxKind.PartialKeyword));

        return new ToolModel
        {
            Namespace = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString(),
            ClassName = type.Name,
            ClassIsPartial = isPartial,
            MethodName = method.Name,
            IsStatic = method.IsStatic,
            ToolName = toolName,
            Description = description,
            ParamTypes = method.Parameters
                .Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .ToArray(),
            ReturnType = method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ReturnsVoid = method.ReturnsVoid,
            IsSupported = !method.IsGenericMethod && method.Parameters.All(p => p.RefKind == RefKind.None),
            ClassLocation = type.Locations.FirstOrDefault(),
            MethodLocation = method.Locations.FirstOrDefault(),
        };
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<ToolModel> models)
    {
        foreach (IGrouping<string, ToolModel> group in models.GroupBy(m => m.Namespace + "." + m.ClassName))
        {
            ToolModel first = group.First();
            if (!first.ClassIsPartial)
            {
                spc.ReportDiagnostic(Diagnostic.Create(NotPartial, first.ClassLocation, first.ClassName));
                continue;
            }

            var tools = new List<ToolModel>();
            var seenNames = new HashSet<string>();
            foreach (ToolModel candidate in group)
            {
                if (!candidate.IsSupported)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(UnsupportedSignature, candidate.MethodLocation, candidate.MethodName));
                    continue;
                }
                string name = candidate.ToolName ?? ToSnakeCase(candidate.MethodName);
                if (!seenNames.Add(name))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(DuplicateName, candidate.MethodLocation, name, candidate.ClassName));
                    continue;
                }
                tools.Add(candidate);
            }
            if (tools.Count == 0)
            {
                continue;
            }

            bool allStatic = tools.All(m => m.IsStatic);
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated by Trellis.Tools.Generator />");
            sb.AppendLine("#nullable enable");
            if (first.Namespace.Length > 0)
            {
                sb.AppendLine("namespace " + first.Namespace + ";");
            }
            sb.AppendLine();
            sb.AppendLine("partial class " + first.ClassName);
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>Tools generated from this class's [Tool] methods.</summary>");
            sb.AppendLine("    public " + (allStatic ? "static " : "")
                + "global::System.Collections.Generic.IReadOnlyList<global::Microsoft.Extensions.AI.AITool> CreateTools() =>");
            sb.AppendLine("        new global::Microsoft.Extensions.AI.AITool[]");
            sb.AppendLine("        {");
            foreach (ToolModel tool in tools)
            {
                sb.AppendLine("            global::Microsoft.Extensions.AI.AIFunctionFactory.Create(");
                sb.AppendLine("                (" + DelegateType(tool) + ")" + tool.MethodName + ",");
                sb.AppendLine("                name: " + Literal(tool.ToolName ?? ToSnakeCase(tool.MethodName)) + ",");
                sb.AppendLine("                description: " + (tool.Description is null ? "null" : Literal(tool.Description)) + "),");
            }
            sb.AppendLine("        };");
            sb.AppendLine("}");

            string hint = (first.Namespace.Length > 0 ? first.Namespace + "." : "") + first.ClassName + ".Tools.g.cs";
            spc.AddSource(hint, SourceText.From(sb.ToString(), Encoding.UTF8));
        }
    }

    private static string DelegateType(ToolModel tool)
    {
        string joined = string.Join(", ", tool.ParamTypes);
        if (tool.ReturnsVoid)
        {
            return tool.ParamTypes.Length == 0
                ? "global::System.Action"
                : "global::System.Action<" + joined + ">";
        }
        return tool.ParamTypes.Length == 0
            ? "global::System.Func<" + tool.ReturnType + ">"
            : "global::System.Func<" + joined + ", " + tool.ReturnType + ">";
    }

    private static string Literal(string value) =>
        SyntaxFactory.Literal(value).ToFullString();

    private static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                {
                    sb.Append('_');
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
