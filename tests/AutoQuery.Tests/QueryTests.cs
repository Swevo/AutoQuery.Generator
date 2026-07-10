using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using AutoQuery;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace AutoQuery.Tests;

public sealed class QueryTests
{
    [Fact]
    public void AttributesAlwaysEmitted()
    {
        var result = RunGenerator(string.Empty);

        Assert.Contains(result.GeneratedSources.Keys, key => key.EndsWith("AutoQuery.Attributes.g.cs", StringComparison.Ordinal));
        Assert.Contains("public sealed class QuerySpecAttribute", result.GetGeneratedSource("AutoQuery.Attributes.g.cs"));
    }

    [Fact]
    public void BasicFilter_StringProperty_ContainsFilter()
    {
        var result = RunGenerator("""
            using AutoQuery;

            public sealed class Product
            {
                public string? Name { get; set; }
            }

            [QuerySpec(typeof(Product))]
            public partial class ProductQuery
            {
                public string? Name { get; set; }
            }
            """);

        result.AssertNoErrors();
        Assert.Contains("query = query.Where(x => x.Name != null && x.Name.Contains(Name));", result.GetGeneratedSource("ProductQuery.AutoQuery.g.cs"));
    }

    [Fact]
    public void BasicFilter_BoolProperty_EqualityFilter()
    {
        var result = RunGenerator("""
            using AutoQuery;

            public sealed class Product
            {
                public bool IsActive { get; set; }
            }

            [QuerySpec(typeof(Product))]
            public partial class ProductQuery
            {
                public bool? IsActive { get; set; }
            }
            """);

        result.AssertNoErrors();
        Assert.Contains("query = query.Where(x => x.IsActive == IsActive.Value);", result.GetGeneratedSource("ProductQuery.AutoQuery.g.cs"));
    }

    [Fact]
    public void BasicFilter_NullableInt_EqualityFilter()
    {
        var result = RunGenerator("""
            using AutoQuery;

            public sealed class Product
            {
                public int CategoryId { get; set; }
            }

            [QuerySpec(typeof(Product))]
            public partial class ProductQuery
            {
                public int? CategoryId { get; set; }
            }
            """);

        result.AssertNoErrors();
        Assert.Contains("query = query.Where(x => x.CategoryId == CategoryId.Value);", result.GetGeneratedSource("ProductQuery.AutoQuery.g.cs"));
    }

    [Fact]
    public void BasicFilter_MinPrefix_GreaterThanOrEqual()
    {
        var result = RunGenerator("""
            using AutoQuery;

            public sealed class Product
            {
                public decimal Price { get; set; }
            }

            [QuerySpec(typeof(Product))]
            public partial class ProductQuery
            {
                public decimal? MinPrice { get; set; }
            }
            """);

        result.AssertNoErrors();
        Assert.Contains("query = query.Where(x => x.Price >= MinPrice.Value);", result.GetGeneratedSource("ProductQuery.AutoQuery.g.cs"));
    }

    [Fact]
    public void BasicFilter_MaxPrefix_LessThanOrEqual()
    {
        var result = RunGenerator("""
            using AutoQuery;

            public sealed class Product
            {
                public decimal Price { get; set; }
            }

            [QuerySpec(typeof(Product))]
            public partial class ProductQuery
            {
                public decimal? MaxPrice { get; set; }
            }
            """);

        result.AssertNoErrors();
        Assert.Contains("query = query.Where(x => x.Price <= MaxPrice.Value);", result.GetGeneratedSource("ProductQuery.AutoQuery.g.cs"));
    }

    [Fact]
    public void CustomFilter_QueryFilterAttribute()
    {
        var result = RunGenerator("""
            using AutoQuery;

            public sealed class Category
            {
                public string? Name { get; set; }
            }

            public sealed class Product
            {
                public Category Category { get; set; } = new();
            }

            [QuerySpec(typeof(Product))]
            public partial class ProductQuery
            {
                [QueryFilter("x => x.Category.Name == value")]
                public string? CategoryName { get; set; }
            }
            """);

        result.AssertNoErrors();
        Assert.Contains("query = query.Where(x => x.Category.Name == CategoryName!);", result.GetGeneratedSource("ProductQuery.AutoQuery.g.cs"));
    }

    [Fact]
    public void QueryIgnore_PropertyExcluded()
    {
        var result = RunGenerator("""
            using AutoQuery;

            public sealed class Product
            {
                public string? Name { get; set; }
                public string? Secret { get; set; }
            }

            [QuerySpec(typeof(Product))]
            public partial class ProductQuery
            {
                public string? Name { get; set; }
                [QueryIgnore]
                public string? Secret { get; set; }
            }
            """);

        result.AssertNoErrors();
        var code = result.GetGeneratedSource("ProductQuery.AutoQuery.g.cs");
        Assert.Contains("x.Name.Contains(Name)", code);
        Assert.DoesNotContain("Secret", code);
    }

    [Fact]
    public void Pagination_PageNumberAndSize_GeneratesSkipTake()
    {
        var result = RunGenerator("""
            using AutoQuery;

            public sealed class Product
            {
                public string? Name { get; set; }
            }

            [QuerySpec(typeof(Product))]
            public partial class ProductQuery
            {
                public string? Name { get; set; }
                public int PageNumber { get; set; } = 1;
                public int PageSize { get; set; } = 20;
            }
            """);

        result.AssertNoErrors();
        Assert.Contains("query = query.Skip((PageNumber - 1) * PageSize).Take(PageSize);", result.GetGeneratedSource("ProductQuery.AutoQuery.g.cs"));
    }

    [Fact]
    public void Sort_QuerySortAttribute_GeneratesSwitchExpression()
    {
        var result = RunGenerator("""
            using AutoQuery;

            public sealed class Product
            {
                public string? Name { get; set; }
                public decimal Price { get; set; }
            }

            [QuerySpec(typeof(Product))]
            public partial class ProductQuery
            {
                public string? Name { get; set; }
                public decimal? Price { get; set; }
                [QuerySort]
                public string? SortBy { get; set; }
                public bool SortDescending { get; set; }
            }
            """);

        result.AssertNoErrors();
        var code = result.GetGeneratedSource("ProductQuery.AutoQuery.g.cs");
        Assert.Contains("query = (SortBy, SortDescending) switch", code);
        Assert.Contains("(\"Name\", false) => query.OrderBy(x => x.Name)", code);
        Assert.Contains("(\"Price\", true)  => query.OrderByDescending(x => x.Price)", code);
    }

    [Fact]
    public void NonPartialClass_EmitsAQ002Error()
    {
        var result = RunGenerator("""
            using AutoQuery;

            public sealed class Product
            {
                public string? Name { get; set; }
            }

            [QuerySpec(typeof(Product))]
            public class ProductQuery
            {
                public string? Name { get; set; }
            }
            """);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "AQ002" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void EntityTypeNotFound_EmitsAQ001Error()
    {
        var result = RunGenerator("""
            using AutoQuery;

            [QuerySpec(typeof(MissingEntity))]
            public partial class ProductQuery
            {
                public string? Name { get; set; }
            }
            """);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "AQ001" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void NoFilterableProperties_EmitsAQ003Warning()
    {
        var result = RunGenerator("""
            using AutoQuery;

            public sealed class Product
            {
                public string? Name { get; set; }
            }

            [QuerySpec(typeof(Product))]
            public partial class ProductQuery
            {
                [QuerySort]
                public string? SortBy { get; set; }
                public bool SortDescending { get; set; }
                public int PageNumber { get; set; } = 1;
                public int PageSize { get; set; } = 20;
            }
            """);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "AQ003" && diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void FullyQualifiedNames_UsedInGeneratedCode()
    {
        var result = RunGenerator("""
            using AutoQuery;

            namespace Demo.Models
            {
                public sealed class Product
                {
                    public string? Name { get; set; }
                }
            }

            namespace Demo.Queries
            {
                [QuerySpec(typeof(Demo.Models.Product))]
                public partial class ProductQuery
                {
                    public string? Name { get; set; }
                }
            }
            """);

        result.AssertNoErrors();
        Assert.Contains("global::Demo.Models.Product", result.GetGeneratedSource("Demo_Queries_ProductQuery.AutoQuery.g.cs"));
    }

    [Fact]
    public void MultipleFilters_AllApplied()
    {
        var result = RunGenerator("""
            using AutoQuery;

            public sealed class Product
            {
                public string? Name { get; set; }
                public decimal Price { get; set; }
                public bool IsActive { get; set; }
            }

            [QuerySpec(typeof(Product))]
            public partial class ProductQuery
            {
                public string? Name { get; set; }
                public decimal? MinPrice { get; set; }
                public bool? IsActive { get; set; }
            }
            """);

        result.AssertNoErrors();
        var code = result.GetGeneratedSource("ProductQuery.AutoQuery.g.cs");
        Assert.Contains("x.Name.Contains(Name)", code);
        Assert.Contains("x.Price >= MinPrice.Value", code);
        Assert.Contains("x.IsActive == IsActive.Value", code);
    }

    [Fact]
    public void PageAttributes_WithCustomNames_AreDetected()
    {
        var result = RunGenerator("""
            using AutoQuery;

            public sealed class Product
            {
                public string? Name { get; set; }
            }

            [QuerySpec(typeof(Product))]
            public partial class ProductQuery
            {
                public string? Name { get; set; }
                [QueryPage] public int ResultsPageNumber { get; set; } = 1;
                [QueryPage] public int ResultsPageSize { get; set; } = 10;
            }
            """);

        result.AssertNoErrors();
        Assert.Contains("query = query.Skip((ResultsPageNumber - 1) * ResultsPageSize).Take(ResultsPageSize);", result.GetGeneratedSource("ProductQuery.AutoQuery.g.cs"));
    }

    [Fact]
    public void RangeSuffixes_FromAndTo_AreTranslated()
    {
        var result = RunGenerator("""
            using System;
            using AutoQuery;

            public sealed class Product
            {
                public DateTime Created { get; set; }
            }

            [QuerySpec(typeof(Product))]
            public partial class ProductQuery
            {
                public DateTime? CreatedFrom { get; set; }
                public DateTime? CreatedTo { get; set; }
            }
            """);

        result.AssertNoErrors();
        var code = result.GetGeneratedSource("ProductQuery.AutoQuery.g.cs");
        Assert.Contains("x.Created >= CreatedFrom.Value", code);
        Assert.Contains("x.Created <= CreatedTo.Value", code);
    }

    [Fact]
    public void QueryBinding_MethodsGeneratedWithoutAspNetDependency()
    {
        var result = RunGenerator("""
            using System;
            using AutoQuery;

            public enum ProductStatus
            {
                Draft,
                Active
            }

            public sealed class Product
            {
                public string? Name { get; set; }
                public decimal Price { get; set; }
                public ProductStatus Status { get; set; }
            }

            [QuerySpec(typeof(Product))]
            public partial class ProductQuery
            {
                public string? Name { get; set; }
                public decimal? MinPrice { get; set; }
                public ProductStatus? Status { get; set; }
                [QuerySort] public string? SortBy { get; set; }
                public bool SortDescending { get; set; }
                public int PageNumber { get; set; } = 1;
                public int PageSize { get; set; } = 20;
            }
            """);

        result.AssertNoErrors();
        var code = result.GetGeneratedSource("ProductQuery.AutoQuery.g.cs");
        Assert.Contains("public void BindFromQuery(global::System.Collections.Generic.IEnumerable<global::System.Collections.Generic.KeyValuePair<string, string?>> query)", code);
        Assert.Contains("public static ProductQuery FromQuery(global::System.Collections.Generic.IEnumerable<global::System.Collections.Generic.KeyValuePair<string, string?>> query)", code);
        Assert.Contains("public static ProductQuery FromQuery<TValue>(global::System.Collections.Generic.IEnumerable<global::System.Collections.Generic.KeyValuePair<string, TValue>> query)", code);
        Assert.Contains("global::System.Decimal.TryParse", code);
        Assert.Contains("global::System.Enum.TryParse<global::ProductStatus>", code);
        Assert.DoesNotContain("Microsoft.AspNetCore", code);
        Assert.DoesNotContain("StringValues", code);
    }

    [Fact]
    public void FromQuery_BindsSupportedScalarValues()
    {
        var result = LoadGeneratedAssembly(QueryBindingSource);
        result.AssertNoErrors();

        var queryType = result.GetRequiredType("ProductQuery");
        var spec = queryType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(static method => method.Name == "FromQuery" && !method.IsGenericMethod)
            .Invoke(null, new object[]
            {
                new Dictionary<string, string?>
                {
                    ["Name"] = "Laptop",
                    ["MinPrice"] = "500.25",
                    ["MaxPrice"] = "1500.50",
                    ["IsActive"] = "true",
                    ["CreatedFrom"] = "2026-01-02T03:04:05Z",
                    ["Status"] = "Active",
                    ["SortBy"] = "Name",
                    ["SortDescending"] = "true",
                    ["PageNumber"] = "2",
                    ["PageSize"] = "10"
                }
            })!;

        Assert.Equal("Laptop", queryType.GetProperty("Name")!.GetValue(spec));
        Assert.Equal(500.25m, queryType.GetProperty("MinPrice")!.GetValue(spec));
        Assert.Equal(1500.50m, queryType.GetProperty("MaxPrice")!.GetValue(spec));
        Assert.Equal(true, queryType.GetProperty("IsActive")!.GetValue(spec));
        Assert.Equal(DateTime.Parse("2026-01-02T03:04:05Z", null, System.Globalization.DateTimeStyles.RoundtripKind), queryType.GetProperty("CreatedFrom")!.GetValue(spec));
        Assert.Equal("Active", queryType.GetProperty("Status")!.GetValue(spec)!.ToString());
        Assert.Equal("Name", queryType.GetProperty("SortBy")!.GetValue(spec));
        Assert.Equal(true, queryType.GetProperty("SortDescending")!.GetValue(spec));
        Assert.Equal(2, queryType.GetProperty("PageNumber")!.GetValue(spec));
        Assert.Equal(10, queryType.GetProperty("PageSize")!.GetValue(spec));
    }

    [Fact]
    public void FromQuery_GenericEnumerableOverload_SkipsMalformedValuesAndUsesFirstEntry()
    {
        var result = LoadGeneratedAssembly(QueryBindingSource);
        result.AssertNoErrors();

        var queryType = result.GetRequiredType("ProductQuery");
        var genericFromQuery = queryType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(static method => method.Name == "FromQuery" && method.IsGenericMethodDefinition)
            .MakeGenericMethod(typeof(string[]));

        var spec = genericFromQuery.Invoke(null, new object[]
        {
            new Dictionary<string, string[]>
            {
                ["name"] = ["Camera"],
                ["minPrice"] = ["not-a-number"],
                ["createdFrom"] = ["2026-06-01T00:00:00Z", "2026-06-02T00:00:00Z"],
                ["status"] = ["not-an-enum"],
                ["sortDescending"] = ["not-a-bool"],
                ["pageNumber"] = ["NaN"],
                ["pageSize"] = ["50"],
                ["unknown"] = ["ignored"]
            }
        })!;

        Assert.Equal("Camera", queryType.GetProperty("Name")!.GetValue(spec));
        Assert.Null(queryType.GetProperty("MinPrice")!.GetValue(spec));
        Assert.Equal(DateTime.Parse("2026-06-01T00:00:00Z", null, System.Globalization.DateTimeStyles.RoundtripKind), queryType.GetProperty("CreatedFrom")!.GetValue(spec));
        Assert.Null(queryType.GetProperty("Status")!.GetValue(spec));
        Assert.Equal(false, queryType.GetProperty("SortDescending")!.GetValue(spec));
        Assert.Equal(1, queryType.GetProperty("PageNumber")!.GetValue(spec));
        Assert.Equal(50, queryType.GetProperty("PageSize")!.GetValue(spec));
    }

    [Fact]
    public void BindFromQuery_AndApply_WorkTogether()
    {
        var result = LoadGeneratedAssembly(QueryBindingSource);
        result.AssertNoErrors();

        var assembly = result.Assembly;
        var queryType = result.GetRequiredType("ProductQuery");
        var query = Activator.CreateInstance(queryType)!;
        queryType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(static method => method.Name == "BindFromQuery" && method.IsGenericMethodDefinition)
            .MakeGenericMethod(typeof(string[]))
            .Invoke(query, new object[]
            {
                new Dictionary<string, string[]>
                {
                    ["name"] = ["Laptop"],
                    ["isActive"] = ["true"],
                    ["sortBy"] = ["Price"],
                    ["sortDescending"] = ["true"],
                    ["pageNumber"] = ["2"],
                    ["pageSize"] = ["1"]
                }
            });

        var entityType = result.GetRequiredType("Product");
        var products = CreateEntityList(
            entityType,
            CreateProduct(entityType, "Laptop Pro", 1500m, true, new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc), "Active"),
            CreateProduct(entityType, "Laptop Air", 900m, true, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), "Active"),
            CreateProduct(entityType, "Mouse", 50m, true, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), "Draft"));

        var queryable = typeof(Queryable)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(static method => method.Name == "AsQueryable" && method.IsGenericMethodDefinition && method.GetParameters().Length == 1)
            .MakeGenericMethod(entityType)
            .Invoke(null, new object[] { products })!;

        var applied = (IEnumerable)queryType.GetMethod("Apply")!.Invoke(query, [queryable])!;
        var results = applied.Cast<object>().ToArray();

        Assert.Single(results);
        Assert.Equal("Laptop Air", entityType.GetProperty("Name")!.GetValue(results[0]));
    }

    private static GeneratedAssemblyTestResult RunGenerator(string userSource)
    {
        const string linqStub = """
            #nullable enable
            namespace System.Linq
            {
                using System;
                using System.Collections.Generic;
                using System.Linq.Expressions;

                public static class Queryable
                {
                    public static IQueryable<T> Where<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate) => source;
                    public static IQueryable<T> OrderBy<T, TKey>(this IQueryable<T> source, Expression<Func<T, TKey>> keySelector) => source;
                    public static IQueryable<T> OrderByDescending<T, TKey>(this IQueryable<T> source, Expression<Func<T, TKey>> keySelector) => source;
                    public static IQueryable<T> Skip<T>(this IQueryable<T> source, int count) => source;
                    public static IQueryable<T> Take<T>(this IQueryable<T> source, int count) => source;
                }

                public interface IQueryable<T> : IEnumerable<T> { }
            }

            namespace System.Linq.Expressions
            {
                public class Expression<TDelegate> { }
            }
            """;

        return RunGeneratorCore(
            "#nullable enable\n" + userSource + "\n" + linqStub,
            GetStubReferences(),
            emitAssembly: false);
    }

    private static GeneratedAssemblyTestResult LoadGeneratedAssembly(string userSource)
        => RunGeneratorCore(
            "#nullable enable\n" + userSource + "\n",
            GetRuntimeReferences(),
            emitAssembly: true);

    private static GeneratedAssemblyTestResult RunGeneratorCore(string sourceText, IEnumerable<MetadataReference> references, bool emitAssembly)
    {
        var source = SourceText.From(sourceText, Encoding.UTF8);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            assemblyName: "AutoQuery.Tests.Generated." + Guid.NewGuid().ToString("N"),
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new AutoQueryGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator.AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updatedCompilation, out _);

        var runResult = driver.GetRunResult();
        var generatedSources = runResult.Results
            .SelectMany(static result => result.GeneratedSources)
            .ToDictionary(static sourceResult => sourceResult.HintName, static sourceResult => sourceResult.SourceText.ToString(), StringComparer.Ordinal);
        var diagnostics = runResult.Diagnostics.AddRange(updatedCompilation.GetDiagnostics());

        if (!emitAssembly)
        {
            return new GeneratedAssemblyTestResult(generatedSources, diagnostics, null);
        }

        using var assemblyStream = new MemoryStream();
        using var symbolsStream = new MemoryStream();
        var emitResult = updatedCompilation.Emit(assemblyStream, symbolsStream);
        Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics));

        assemblyStream.Position = 0;
        symbolsStream.Position = 0;
        var loadContext = new AssemblyLoadContext("AutoQuery.Tests." + Guid.NewGuid().ToString("N"), isCollectible: true);
        var assembly = loadContext.LoadFromStream(assemblyStream, symbolsStream);

        return new GeneratedAssemblyTestResult(generatedSources, diagnostics, assembly);
    }

    private static IEnumerable<MetadataReference> GetStubReferences()
    {
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        yield return MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location);
        yield return MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location);
    }

    private static IEnumerable<MetadataReference> GetRuntimeReferences()
        => ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path));

    private static IList CreateEntityList(Type entityType, params object[] items)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entityType))!;
        foreach (var item in items)
        {
            list.Add(item);
        }

        return list;
    }

    private static object CreateProduct(Type entityType, string name, decimal price, bool isActive, DateTime created, string status)
    {
        var instance = Activator.CreateInstance(entityType)!;
        entityType.GetProperty("Name")!.SetValue(instance, name);
        entityType.GetProperty("Price")!.SetValue(instance, price);
        entityType.GetProperty("IsActive")!.SetValue(instance, isActive);
        entityType.GetProperty("Created")!.SetValue(instance, created);
        entityType.GetProperty("Status")!.SetValue(instance, Enum.Parse(entityType.Assembly.GetType("ProductStatus")!, status));
        return instance;
    }

    private const string QueryBindingSource = """
        using System;
        using AutoQuery;

        public enum ProductStatus
        {
            Draft,
            Active
        }

        public sealed class Product
        {
            public string? Name { get; set; }
            public decimal Price { get; set; }
            public bool IsActive { get; set; }
            public DateTime Created { get; set; }
            public ProductStatus Status { get; set; }
        }

        [QuerySpec(typeof(Product))]
        public partial class ProductQuery
        {
            public string? Name { get; set; }
            public decimal? MinPrice { get; set; }
            public decimal? MaxPrice { get; set; }
            public bool? IsActive { get; set; }
            public DateTime? CreatedFrom { get; set; }
            public ProductStatus? Status { get; set; }
            [QuerySort] public string? SortBy { get; set; }
            public bool SortDescending { get; set; }
            public int PageNumber { get; set; } = 1;
            public int PageSize { get; set; } = 20;
        }
        """;

    private record GeneratedAssemblyTestResult(
        IReadOnlyDictionary<string, string> GeneratedSources,
        ImmutableArray<Diagnostic> Diagnostics,
        Assembly? Assembly)
    {
        public Type GetRequiredType(string typeName)
            => Assembly?.GetType(typeName) ?? throw new InvalidOperationException($"Type '{typeName}' was not loaded.");

        public string GetGeneratedSource(string hintName)
            => GeneratedSources.FirstOrDefault(pair => pair.Key.EndsWith(hintName, StringComparison.Ordinal)).Value ?? string.Empty;

        public void AssertNoErrors()
        {
            var errors = Diagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
            Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(static error => error.ToString())));
        }
    }
}
