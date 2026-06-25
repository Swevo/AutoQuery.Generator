using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
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

    private static GeneratorTestResult RunGenerator(string userSource)
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

        var source = SourceText.From("#nullable enable\n" + userSource + "\n" + linqStub, Encoding.UTF8);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            assemblyName: "AutoQuery.Tests.Generated",
            syntaxTrees: new[] { syntaxTree },
            references: GetReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new AutoQueryGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator.AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updatedCompilation, out _);

        var runResult = driver.GetRunResult();
        var generatedSources = runResult.Results
            .SelectMany(static result => result.GeneratedSources)
            .ToDictionary(static sourceResult => sourceResult.HintName, static sourceResult => sourceResult.SourceText.ToString(), StringComparer.Ordinal);

        return new GeneratorTestResult(
            generatedSources,
            runResult.Diagnostics.AddRange(updatedCompilation.GetDiagnostics()));
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        yield return MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location);
        yield return MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location);
    }

    private sealed record GeneratorTestResult(
        IReadOnlyDictionary<string, string> GeneratedSources,
        ImmutableArray<Diagnostic> Diagnostics)
    {
        public string GetGeneratedSource(string hintName)
            => GeneratedSources.FirstOrDefault(pair => pair.Key.EndsWith(hintName, StringComparison.Ordinal)).Value ?? string.Empty;

        public void AssertNoErrors()
        {
            var errors = Diagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
            Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(static error => error.ToString())));
        }
    }
}
