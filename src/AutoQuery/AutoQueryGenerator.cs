using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AutoQuery;

[Generator(LanguageNames.CSharp)]
public sealed class AutoQueryGenerator : IIncrementalGenerator
{
    private const string QuerySpecAttributeName = "AutoQuery.QuerySpecAttribute";
    private const string QueryFilterAttributeName = "AutoQuery.QueryFilterAttribute";
    private const string QueryIgnoreAttributeName = "AutoQuery.QueryIgnoreAttribute";
    private const string QuerySortAttributeName = "AutoQuery.QuerySortAttribute";
    private const string QueryPageAttributeName = "AutoQuery.QueryPageAttribute";

    private static readonly DiagnosticDescriptor EntityTypeNotResolved = new(
        id: "AQ001",
        title: "QuerySpec entity type could not be resolved",
        messageFormat: "[QuerySpec] entity type could not be resolved for '{0}'",
        category: "AutoQuery",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor QuerySpecMustBePartial = new(
        id: "AQ002",
        title: "QuerySpec class must be partial",
        messageFormat: "[QuerySpec] class '{0}' must be declared partial",
        category: "AutoQuery",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NoFilterableProperties = new(
        id: "AQ003",
        title: "QuerySpec has no filterable properties",
        messageFormat: "Query spec '{0}' has no filterable properties",
        category: "AutoQuery",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static output =>
            output.AddSource("AutoQuery.Attributes.g.cs", SourceText.From(AttributeSource, Encoding.UTF8)));

        var specs = context.SyntaxProvider.ForAttributeWithMetadataName(
            QuerySpecAttributeName,
            static (node, _) => node is ClassDeclarationSyntax,
            static (syntaxContext, cancellationToken) => BuildModel(syntaxContext, cancellationToken));

        context.RegisterSourceOutput(specs, static (productionContext, model) => Emit(productionContext, model));
    }

    private static QuerySpecModel BuildModel(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var typeSymbol = (INamedTypeSymbol)context.TargetSymbol;
        var typeDeclaration = (ClassDeclarationSyntax)context.TargetNode;
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        var querySpecAttribute = context.Attributes.FirstOrDefault(static a =>
            a.AttributeClass?.ToDisplayString() == QuerySpecAttributeName);

        var entityType = querySpecAttribute?.ConstructorArguments.Length > 0
            ? querySpecAttribute.ConstructorArguments[0].Value as ITypeSymbol
            : null;

        if (entityType is null || entityType.TypeKind == TypeKind.Error)
        {
            diagnostics.Add(Diagnostic.Create(EntityTypeNotResolved, typeDeclaration.Identifier.GetLocation(), typeSymbol.Name));
        }

        var isPartial = typeDeclaration.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PartialKeyword));
        if (!isPartial)
        {
            diagnostics.Add(Diagnostic.Create(QuerySpecMustBePartial, typeDeclaration.Identifier.GetLocation(), typeSymbol.Name));
        }

        var properties = typeSymbol
            .GetMembers()
            .OfType<IPropertySymbol>()
            .Where(static property =>
                !property.IsStatic &&
                property.Parameters.Length == 0 &&
                property.DeclaredAccessibility == Accessibility.Public)
            .Select(CreatePropertyModel)
            .ToImmutableArray();

        var filterProperties = properties
            .Where(static property => property.FilterKind != FilterKind.None)
            .ToImmutableArray();

        if (filterProperties.Length == 0)
        {
            diagnostics.Add(Diagnostic.Create(NoFilterableProperties, typeDeclaration.Identifier.GetLocation(), typeSymbol.Name));
        }

        var sortProperty = properties.FirstOrDefault(static property => property.IsSort && property.IsString);
        var descendingProperty = properties.FirstOrDefault(static property => property.IsDescendingFlag);
        var sortFields = properties
            .Where(static property =>
                !property.IsIgnored &&
                !property.IsSort &&
                !property.IsPageNumber &&
                !property.IsPageSize &&
                !property.IsDescendingFlag)
            .Select(static property => property.SortTargetName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();

        var pageNumberProperty = properties.FirstOrDefault(static property => property.IsPageNumber);
        var pageSizeProperty = properties.FirstOrDefault(static property => property.IsPageSize);
        var canInstantiate = !typeSymbol.IsAbstract &&
                             typeSymbol.InstanceConstructors.Any(static constructor => constructor.Parameters.Length == 0);

        return new QuerySpecModel(
            typeSymbol.ContainingNamespace.IsGlobalNamespace ? null : typeSymbol.ContainingNamespace.ToDisplayString(),
            typeSymbol.Name,
            typeSymbol.TypeParameters.Length == 0
                ? string.Empty
                : "<" + string.Join(", ", typeSymbol.TypeParameters.Select(static parameter => parameter.Name)) + ">",
            entityType is null || entityType.TypeKind == TypeKind.Error
                ? string.Empty
                : entityType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            GetHintName(typeSymbol),
            isPartial && entityType is not null && entityType.TypeKind != TypeKind.Error,
            canInstantiate,
            diagnostics.ToImmutable(),
            properties,
            filterProperties,
            sortProperty?.Name,
            descendingProperty?.Name,
            sortFields,
            pageNumberProperty?.Name,
            pageSizeProperty?.Name);
    }

    private static PropertyModel CreatePropertyModel(IPropertySymbol property)
    {
        var isIgnored = HasAttribute(property, QueryIgnoreAttributeName);
        var isSort = HasAttribute(property, QuerySortAttributeName);
        var hasPageAttribute = HasAttribute(property, QueryPageAttributeName);
        var queryFilterExpression = GetQueryFilterExpression(property);

        var isString = property.Type.SpecialType == SpecialType.System_String;
        var isNullableValueType = property.Type is INamedTypeSymbol namedType &&
                                  namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
        var isInt32 = property.Type.SpecialType == SpecialType.System_Int32;
        var isPageNumber = isInt32 &&
                           (string.Equals(property.Name, "PageNumber", StringComparison.Ordinal) ||
                            (hasPageAttribute && ContainsAll(property.Name, "Page", "Number")));
        var isPageSize = isInt32 &&
                         (string.Equals(property.Name, "PageSize", StringComparison.Ordinal) ||
                          (hasPageAttribute && ContainsAll(property.Name, "Page", "Size")));
        var isDescendingFlag = property.Type.SpecialType == SpecialType.System_Boolean &&
                               (string.Equals(property.Name, "SortDescending", StringComparison.Ordinal) ||
                                string.Equals(property.Name, "IsDescending", StringComparison.Ordinal));
        var binding = CreateBindingModel(property, isIgnored);

        var filterKind = FilterKind.None;
        if (!isIgnored && !isSort && !isPageNumber && !isPageSize)
        {
            if (queryFilterExpression is not null)
            {
                filterKind = FilterKind.Custom;
            }
            else if (isString)
            {
                filterKind = FilterKind.StringContains;
            }
            else if (isNullableValueType)
            {
                filterKind = FilterKind.NullableValue;
            }
        }

        return new PropertyModel(
            property.Name,
            filterKind,
            isIgnored,
            isSort,
            isPageNumber,
            isPageSize,
            isDescendingFlag,
            isString,
            binding.Kind,
            binding.TypeName,
            queryFilterExpression,
            GetFilterTargetName(property.Name),
            GetFilterTargetName(property.Name));
    }

    private static BindingModel CreateBindingModel(IPropertySymbol property, bool isIgnored)
    {
        if (isIgnored ||
            property.SetMethod is null ||
            property.SetMethod.DeclaredAccessibility != Accessibility.Public ||
            property.SetMethod.IsInitOnly)
        {
            return BindingModel.None;
        }

        if (property.Type.SpecialType == SpecialType.System_String)
        {
            return new BindingModel(BindingKind.String, null);
        }

        var targetType = property.Type;
        if (property.Type is INamedTypeSymbol namedType &&
            namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            targetType = namedType.TypeArguments[0];
        }

        if (targetType.TypeKind == TypeKind.Enum)
        {
            return new BindingModel(BindingKind.Enum, targetType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }

        return targetType.SpecialType switch
        {
            SpecialType.System_Boolean => new BindingModel(BindingKind.Boolean, null),
            SpecialType.System_Byte => new BindingModel(BindingKind.Byte, null),
            SpecialType.System_SByte => new BindingModel(BindingKind.SByte, null),
            SpecialType.System_Int16 => new BindingModel(BindingKind.Int16, null),
            SpecialType.System_UInt16 => new BindingModel(BindingKind.UInt16, null),
            SpecialType.System_Int32 => new BindingModel(BindingKind.Int32, null),
            SpecialType.System_UInt32 => new BindingModel(BindingKind.UInt32, null),
            SpecialType.System_Int64 => new BindingModel(BindingKind.Int64, null),
            SpecialType.System_UInt64 => new BindingModel(BindingKind.UInt64, null),
            SpecialType.System_Single => new BindingModel(BindingKind.Single, null),
            SpecialType.System_Double => new BindingModel(BindingKind.Double, null),
            SpecialType.System_Decimal => new BindingModel(BindingKind.Decimal, null),
            SpecialType.System_DateTime => new BindingModel(BindingKind.DateTime, null),
            _ => BindingModel.None,
        };
    }

    private static void Emit(SourceProductionContext context, QuerySpecModel model)
    {
        foreach (var diagnostic in model.Diagnostics)
        {
            context.ReportDiagnostic(diagnostic);
        }

        if (!model.CanGenerate)
        {
            return;
        }

        context.AddSource(model.HintName, SourceText.From(GenerateSource(model), Encoding.UTF8));
    }

    private static string GenerateSource(QuerySpecModel model)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("using System.Linq;");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(model.Namespace))
        {
            builder.Append("namespace ").Append(model.Namespace).AppendLine();
            builder.AppendLine("{");
        }

        var indent = string.IsNullOrWhiteSpace(model.Namespace) ? string.Empty : "    ";
        builder.Append(indent).Append("public partial class ").Append(model.TypeName).Append(model.TypeParameters).AppendLine();
        builder.Append(indent).AppendLine("{");
        builder.Append(indent).Append("    public global::System.Linq.IQueryable<").Append(model.EntityTypeName).Append("> Apply(global::System.Linq.IQueryable<").Append(model.EntityTypeName).AppendLine("> query)");
        builder.Append(indent).AppendLine("    {");

        foreach (var property in model.FilterProperties)
        {
            switch (property.FilterKind)
            {
                case FilterKind.StringContains:
                    builder.Append(indent).Append("        if (").Append(property.Name).AppendLine(" is not null)");
                    builder.Append(indent).Append("            query = query.Where(x => x.").Append(property.FilterTargetName).Append(" != null && x.").Append(property.FilterTargetName).Append(".Contains(").Append(property.Name).AppendLine("));");
                    break;
                case FilterKind.NullableValue:
                    builder.Append(indent).Append("        if (").Append(property.Name).AppendLine(" is not null)");
                    builder.Append(indent).Append("            query = query.Where(x => x.").Append(property.FilterTargetName).Append(' ').Append(GetComparisonOperator(property.Name)).Append(' ').Append(property.Name).AppendLine(".Value);");
                    break;
                case FilterKind.Custom:
                    builder.Append(indent).Append("        if (").Append(property.Name).AppendLine(" is not null)");
                    builder.Append(indent).Append("            query = query.Where(").Append(ReplaceValueToken(property.CustomExpression!, property.Name)).AppendLine(");");
                    break;
            }
        }

        if (model.SortPropertyName is not null && model.DescendingPropertyName is not null && model.SortFields.Length > 0)
        {
            builder.Append(indent).Append("        if (").Append(model.SortPropertyName).AppendLine(" is not null)");
            builder.Append(indent).AppendLine("        {");
            builder.Append(indent).Append("            query = (").Append(model.SortPropertyName).Append(", ").Append(model.DescendingPropertyName).AppendLine(") switch");
            builder.Append(indent).AppendLine("            {");
            foreach (var sortField in model.SortFields)
            {
                builder.Append(indent).Append("                (\"").Append(sortField).Append("\", false) => query.OrderBy(x => x.").Append(sortField).AppendLine("),");
                builder.Append(indent).Append("                (\"").Append(sortField).Append("\", true)  => query.OrderByDescending(x => x.").Append(sortField).AppendLine("),");
            }

            builder.Append(indent).AppendLine("                _ => query");
            builder.Append(indent).AppendLine("            };");
            builder.Append(indent).AppendLine("        }");
        }

        if (model.PageNumberPropertyName is not null && model.PageSizePropertyName is not null)
        {
            builder.Append(indent).Append("        query = query.Skip((").Append(model.PageNumberPropertyName).Append(" - 1) * ").Append(model.PageSizePropertyName).Append(").Take(").Append(model.PageSizePropertyName).AppendLine(");");
        }

        builder.Append(indent).AppendLine("        return query;");
        builder.Append(indent).AppendLine("    }");
        builder.AppendLine();
        builder.Append(indent).AppendLine("    public void BindFromQuery(global::System.Collections.Generic.IEnumerable<global::System.Collections.Generic.KeyValuePair<string, string?>> query)");
        builder.Append(indent).AppendLine("    {");
        builder.Append(indent).AppendLine("        foreach (var item in query)");
        builder.Append(indent).AppendLine("        {");
        builder.Append(indent).AppendLine("            ApplyQueryValue(item.Key, item.Value);");
        builder.Append(indent).AppendLine("        }");
        builder.Append(indent).AppendLine("    }");
        builder.AppendLine();
        builder.Append(indent).AppendLine("    public void BindFromQuery<TValue>(global::System.Collections.Generic.IEnumerable<global::System.Collections.Generic.KeyValuePair<string, TValue>> query)");
        builder.Append(indent).AppendLine("        where TValue : global::System.Collections.Generic.IEnumerable<string>");
        builder.Append(indent).AppendLine("    {");
        builder.Append(indent).AppendLine("        foreach (var item in query)");
        builder.Append(indent).AppendLine("        {");
        builder.Append(indent).AppendLine("            if (TryGetFirstQueryValue(item.Value, out var value))");
        builder.Append(indent).AppendLine("            {");
        builder.Append(indent).AppendLine("                ApplyQueryValue(item.Key, value);");
        builder.Append(indent).AppendLine("            }");
        builder.Append(indent).AppendLine("        }");
        builder.Append(indent).AppendLine("    }");

        if (model.CanInstantiate)
        {
            builder.AppendLine();
            builder.Append(indent).Append("    public static ").Append(model.TypeName).Append(model.TypeParameters).AppendLine(" FromQuery(global::System.Collections.Generic.IEnumerable<global::System.Collections.Generic.KeyValuePair<string, string?>> query)");
            builder.Append(indent).AppendLine("    {");
            builder.Append(indent).Append("        var result = new ").Append(model.TypeName).Append(model.TypeParameters).AppendLine("();");
            builder.Append(indent).AppendLine("        result.BindFromQuery(query);");
            builder.Append(indent).AppendLine("        return result;");
            builder.Append(indent).AppendLine("    }");
            builder.AppendLine();
            builder.Append(indent).Append("    public static ").Append(model.TypeName).Append(model.TypeParameters).AppendLine(" FromQuery<TValue>(global::System.Collections.Generic.IEnumerable<global::System.Collections.Generic.KeyValuePair<string, TValue>> query)");
            builder.Append(indent).AppendLine("        where TValue : global::System.Collections.Generic.IEnumerable<string>");
            builder.Append(indent).AppendLine("    {");
            builder.Append(indent).Append("        var result = new ").Append(model.TypeName).Append(model.TypeParameters).AppendLine("();");
            builder.Append(indent).AppendLine("        result.BindFromQuery(query);");
            builder.Append(indent).AppendLine("        return result;");
            builder.Append(indent).AppendLine("    }");
        }

        builder.AppendLine();
        builder.Append(indent).AppendLine("    private void ApplyQueryValue(string key, string? value)");
        builder.Append(indent).AppendLine("    {");
        foreach (var property in model.Properties.Where(static property => property.BindingKind != BindingKind.None))
        {
            builder.Append(indent).Append("        if (global::System.StringComparer.OrdinalIgnoreCase.Equals(key, \"").Append(property.Name).AppendLine("\"))");
            builder.Append(indent).AppendLine("        {");
            AppendBindingAssignment(builder, indent, property);
            builder.Append(indent).AppendLine("            return;");
            builder.Append(indent).AppendLine("        }");
            builder.AppendLine();
        }

        builder.Append(indent).AppendLine("    }");
        builder.AppendLine();
        builder.Append(indent).AppendLine("    private static bool TryGetFirstQueryValue(global::System.Collections.Generic.IEnumerable<string> values, out string? value)");
        builder.Append(indent).AppendLine("    {");
        builder.Append(indent).AppendLine("        foreach (var item in values)");
        builder.Append(indent).AppendLine("        {");
        builder.Append(indent).AppendLine("            value = item;");
        builder.Append(indent).AppendLine("            return true;");
        builder.Append(indent).AppendLine("        }");
        builder.AppendLine();
        builder.Append(indent).AppendLine("        value = null;");
        builder.Append(indent).AppendLine("        return false;");
        builder.Append(indent).AppendLine("    }");
        builder.Append(indent).AppendLine("}");

        if (!string.IsNullOrWhiteSpace(model.Namespace))
        {
            builder.AppendLine("}");
        }

        return builder.ToString();
    }

    private static void AppendBindingAssignment(StringBuilder builder, string indent, PropertyModel property)
    {
        switch (property.BindingKind)
        {
            case BindingKind.String:
                builder.Append(indent).AppendLine("            if (value is not null)");
                builder.Append(indent).Append("                ").Append(property.Name).AppendLine(" = value;");
                break;
            case BindingKind.Boolean:
                AppendTryParse(builder, indent, property, "global::System.Boolean.TryParse(value, out var parsed)");
                break;
            case BindingKind.Byte:
                AppendTryParse(builder, indent, property, "global::System.Byte.TryParse(value, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var parsed)");
                break;
            case BindingKind.SByte:
                AppendTryParse(builder, indent, property, "global::System.SByte.TryParse(value, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var parsed)");
                break;
            case BindingKind.Int16:
                AppendTryParse(builder, indent, property, "global::System.Int16.TryParse(value, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var parsed)");
                break;
            case BindingKind.UInt16:
                AppendTryParse(builder, indent, property, "global::System.UInt16.TryParse(value, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var parsed)");
                break;
            case BindingKind.Int32:
                AppendTryParse(builder, indent, property, "global::System.Int32.TryParse(value, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var parsed)");
                break;
            case BindingKind.UInt32:
                AppendTryParse(builder, indent, property, "global::System.UInt32.TryParse(value, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var parsed)");
                break;
            case BindingKind.Int64:
                AppendTryParse(builder, indent, property, "global::System.Int64.TryParse(value, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var parsed)");
                break;
            case BindingKind.UInt64:
                AppendTryParse(builder, indent, property, "global::System.UInt64.TryParse(value, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var parsed)");
                break;
            case BindingKind.Single:
                AppendTryParse(builder, indent, property, "global::System.Single.TryParse(value, global::System.Globalization.NumberStyles.Float | global::System.Globalization.NumberStyles.AllowThousands, global::System.Globalization.CultureInfo.InvariantCulture, out var parsed)");
                break;
            case BindingKind.Double:
                AppendTryParse(builder, indent, property, "global::System.Double.TryParse(value, global::System.Globalization.NumberStyles.Float | global::System.Globalization.NumberStyles.AllowThousands, global::System.Globalization.CultureInfo.InvariantCulture, out var parsed)");
                break;
            case BindingKind.Decimal:
                AppendTryParse(builder, indent, property, "global::System.Decimal.TryParse(value, global::System.Globalization.NumberStyles.Number, global::System.Globalization.CultureInfo.InvariantCulture, out var parsed)");
                break;
            case BindingKind.DateTime:
                AppendTryParse(builder, indent, property, "global::System.DateTime.TryParse(value, global::System.Globalization.CultureInfo.InvariantCulture, global::System.Globalization.DateTimeStyles.RoundtripKind | global::System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var parsed)");
                break;
            case BindingKind.Enum:
                AppendTryParse(builder, indent, property, "global::System.Enum.TryParse<" + property.BindingTypeName + ">(value, true, out var parsed)");
                break;
        }
    }

    private static void AppendTryParse(StringBuilder builder, string indent, PropertyModel property, string tryParseExpression)
    {
        builder.Append(indent).Append("            if (").Append(tryParseExpression).AppendLine(")");
        builder.Append(indent).Append("                ").Append(property.Name).AppendLine(" = parsed;");
    }

    private static bool HasAttribute(IPropertySymbol property, string fullName)
        => property.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == fullName);

    private static string? GetQueryFilterExpression(IPropertySymbol property)
        => property.GetAttributes()
            .FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == QueryFilterAttributeName)?
            .ConstructorArguments.FirstOrDefault().Value as string;

    private static bool ContainsAll(string value, string left, string right)
        => value.Contains(left, StringComparison.OrdinalIgnoreCase) &&
           value.Contains(right, StringComparison.OrdinalIgnoreCase);

    private static string GetFilterTargetName(string propertyName)
    {
        if (propertyName.StartsWith("Min", StringComparison.Ordinal) && propertyName.Length > 3)
        {
            return propertyName.Substring(3);
        }

        if (propertyName.StartsWith("Max", StringComparison.Ordinal) && propertyName.Length > 3)
        {
            return propertyName.Substring(3);
        }

        if (propertyName.EndsWith("From", StringComparison.Ordinal) && propertyName.Length > 4)
        {
            return propertyName.Substring(0, propertyName.Length - 4);
        }

        if (propertyName.EndsWith("To", StringComparison.Ordinal) && propertyName.Length > 2)
        {
            return propertyName.Substring(0, propertyName.Length - 2);
        }

        return propertyName;
    }

    private static string GetComparisonOperator(string propertyName)
    {
        if (propertyName.StartsWith("Min", StringComparison.Ordinal) ||
            propertyName.EndsWith("From", StringComparison.Ordinal))
        {
            return ">=";
        }

        if (propertyName.StartsWith("Max", StringComparison.Ordinal) ||
            propertyName.EndsWith("To", StringComparison.Ordinal))
        {
            return "<=";
        }

        return "==";
    }

    private static string ReplaceValueToken(string expression, string propertyName)
        => Regex.Replace(expression, "\\bvalue\\b", propertyName + "!");

    private static string GetHintName(INamedTypeSymbol typeSymbol)
    {
        var name = typeSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            .Replace('<', '_')
            .Replace('>', '_')
            .Replace('.', '_');
        return name + ".AutoQuery.g.cs";
    }

    private sealed class QuerySpecModel
    {
        public QuerySpecModel(
            string? @namespace,
            string typeName,
            string typeParameters,
            string entityTypeName,
            string hintName,
            bool canGenerate,
            bool canInstantiate,
            ImmutableArray<Diagnostic> diagnostics,
            ImmutableArray<PropertyModel> properties,
            ImmutableArray<PropertyModel> filterProperties,
            string? sortPropertyName,
            string? descendingPropertyName,
            ImmutableArray<string> sortFields,
            string? pageNumberPropertyName,
            string? pageSizePropertyName)
        {
            Namespace = @namespace;
            TypeName = typeName;
            TypeParameters = typeParameters;
            EntityTypeName = entityTypeName;
            HintName = hintName;
            CanGenerate = canGenerate;
            CanInstantiate = canInstantiate;
            Diagnostics = diagnostics;
            Properties = properties;
            FilterProperties = filterProperties;
            SortPropertyName = sortPropertyName;
            DescendingPropertyName = descendingPropertyName;
            SortFields = sortFields;
            PageNumberPropertyName = pageNumberPropertyName;
            PageSizePropertyName = pageSizePropertyName;
        }

        public string? Namespace { get; }
        public string TypeName { get; }
        public string TypeParameters { get; }
        public string EntityTypeName { get; }
        public string HintName { get; }
        public bool CanGenerate { get; }
        public bool CanInstantiate { get; }
        public ImmutableArray<Diagnostic> Diagnostics { get; }
        public ImmutableArray<PropertyModel> Properties { get; }
        public ImmutableArray<PropertyModel> FilterProperties { get; }
        public string? SortPropertyName { get; }
        public string? DescendingPropertyName { get; }
        public ImmutableArray<string> SortFields { get; }
        public string? PageNumberPropertyName { get; }
        public string? PageSizePropertyName { get; }
    }

    private sealed class PropertyModel
    {
        public PropertyModel(
            string name,
            FilterKind filterKind,
            bool isIgnored,
            bool isSort,
            bool isPageNumber,
            bool isPageSize,
            bool isDescendingFlag,
            bool isString,
            BindingKind bindingKind,
            string? bindingTypeName,
            string? customExpression,
            string filterTargetName,
            string sortTargetName)
        {
            Name = name;
            FilterKind = filterKind;
            IsIgnored = isIgnored;
            IsSort = isSort;
            IsPageNumber = isPageNumber;
            IsPageSize = isPageSize;
            IsDescendingFlag = isDescendingFlag;
            IsString = isString;
            BindingKind = bindingKind;
            BindingTypeName = bindingTypeName;
            CustomExpression = customExpression;
            FilterTargetName = filterTargetName;
            SortTargetName = sortTargetName;
        }

        public string Name { get; }
        public FilterKind FilterKind { get; }
        public bool IsIgnored { get; }
        public bool IsSort { get; }
        public bool IsPageNumber { get; }
        public bool IsPageSize { get; }
        public bool IsDescendingFlag { get; }
        public bool IsString { get; }
        public BindingKind BindingKind { get; }
        public string? BindingTypeName { get; }
        public string? CustomExpression { get; }
        public string FilterTargetName { get; }
        public string SortTargetName { get; }
    }

    private readonly struct BindingModel
    {
        public static BindingModel None => new(BindingKind.None, null);

        public BindingModel(BindingKind kind, string? typeName)
        {
            Kind = kind;
            TypeName = typeName;
        }

        public BindingKind Kind { get; }
        public string? TypeName { get; }
    }

    private enum FilterKind
    {
        None,
        StringContains,
        NullableValue,
        Custom,
    }

    private enum BindingKind
    {
        None,
        String,
        Boolean,
        Byte,
        SByte,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Single,
        Double,
        Decimal,
        DateTime,
        Enum,
    }

    private const string AttributeSource = @"// <auto-generated/>
#nullable enable
using System;

namespace AutoQuery
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class QuerySpecAttribute : Attribute
    {
        public Type EntityType { get; }
        public QuerySpecAttribute(Type entityType) => EntityType = entityType;
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class QueryFilterAttribute : Attribute
    {
        public string Expression { get; }
        public QueryFilterAttribute(string expression) => Expression = expression;
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class QueryIgnoreAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class QuerySortAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class QueryPageAttribute : Attribute { }
}
";
}
