# AutoQuery.Generator

[![NuGet](https://img.shields.io/nuget/v/AutoQuery.Generator.svg)](https://www.nuget.org/packages/AutoQuery.Generator)
[![NuGet Downloads](https://img.shields.io/nuget/dt/AutoQuery.Generator.svg)](https://www.nuget.org/packages/AutoQuery.Generator)
[![CI](https://github.com/Swevo/AutoQuery.Generator/actions/workflows/build.yml/badge.svg)](https://github.com/Swevo/AutoQuery.Generator/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**[📖 Documentation site](https://swevo.github.io/AutoQuery.Generator/) · [NuGet](https://www.nuget.org/packages/AutoQuery.Generator) · [Changelog](CHANGELOG.md)**

**Compile-time query composition for `IQueryable<T>` via Roslyn incremental source generators.**

Add `[QuerySpec(typeof(Product))]` to a partial query class and AutoQuery.Generator emits strongly-typed `Apply(IQueryable<Product>)`, `BindFromQuery(...)`, and `FromQuery(...)` methods at build time. No reflection. No runtime scanning. AOT-friendly.

```csharp
using AutoQuery;
using System.Linq;

[QuerySpec(typeof(Product))]
public partial class ProductQuery
{
    public string? Name { get; set; }
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
    [QuerySort] public string? SortBy { get; set; }
    public bool SortDescending { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

var filtered = new ProductQuery
{
    Name = "Laptop",
    MinPrice = 500,
    SortBy = "Name",
    PageNumber = 1,
    PageSize = 10,
}.Apply(products);

// Or bind directly from HTTP query-string values:
var requestQuery = new Dictionary<string, string?>
{
    ["Name"] = "Laptop",
    ["MinPrice"] = "500",
    ["SortBy"] = "Name",
    ["PageNumber"] = "1",
    ["PageSize"] = "10"
};

var bound = ProductQuery.FromQuery(requestQuery);
var filteredFromQuery = bound.Apply(products);
```

---

## Installation

```bash
dotnet add package AutoQuery.Generator
```

Targets `netstandard2.0` and works with modern SDK-style .NET projects.

---

## Quick start

### 1. Define an entity and query spec

```csharp
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
```

### 2. Use the generated `Apply` method

```csharp
IQueryable<Product> query = dbContext.Products;
var spec = new ProductQuery { Name = "Phone", MinPrice = 100, IsActive = true };
var result = spec.Apply(query);
```

### 3. Bind directly from query-string values

```csharp
using AutoQuery;

[QuerySpec(typeof(Product))]
public partial class ProductQuery
{
    public string? Name { get; set; }
    public decimal? MinPrice { get; set; }
    [QuerySort] public string? SortBy { get; set; }
    public bool SortDescending { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

// Works with Dictionary<string, string?>
var query = ProductQuery.FromQuery(new Dictionary<string, string?>
{
    ["Name"] = "Laptop",
    ["MinPrice"] = "500",
    ["SortBy"] = "Name",
    ["PageNumber"] = "1",
    ["PageSize"] = "10"
});

// Also works with ASP.NET Core Request.Query without taking an ASP.NET Core package dependency.
app.MapGet("/products", (AppDbContext db, HttpRequest request) =>
{
    var spec = ProductQuery.FromQuery(request.Query);
    return spec.Apply(db.Products);
});
```

Unknown keys are ignored, malformed values are skipped, and successful conversions use invariant culture for numeric/date parsing plus case-insensitive enum parsing.

Generated output resembles:

```csharp
public partial class ProductQuery
{
    public IQueryable<global::YourApp.Product> Apply(IQueryable<global::YourApp.Product> query)
    {
        if (Name is not null)
            query = query.Where(x => x.Name != null && x.Name.Contains(Name));
        if (MinPrice is not null)
            query = query.Where(x => x.Price >= MinPrice.Value);
        if (IsActive is not null)
            query = query.Where(x => x.IsActive == IsActive.Value);
        return query;
    }
}
```

---

## Attributes

### `[QuerySpec(typeof(TEntity))]`
Marks a partial class as a query spec for the target entity.

```csharp
[QuerySpec(typeof(Order))]
public partial class OrderQuery { }
```

### `[QueryFilter("x => x.Category.Name == value")]`
Overrides the default convention and uses your custom LINQ predicate expression. The token `value` is replaced with the query property access.

```csharp
[QueryFilter("x => x.Category.Name == value")]
public string? CategoryName { get; set; }
```

### `[QueryIgnore]`
Skips a property entirely.

```csharp
[QueryIgnore]
public string? DebugOnly { get; set; }
```

### `[QuerySort]`
Marks a `string?` property as the requested sort field.

```csharp
[QuerySort]
public string? SortBy { get; set; }
public bool SortDescending { get; set; }
```

### `[QueryPage]`
Marks pagination properties when you are not using the conventional names `PageNumber` and `PageSize`.

```csharp
[QueryPage] public int ResultsPageNumber { get; set; } = 1;
[QueryPage] public int ResultsPageSize { get; set; } = 25;
```

---

## Convention-based filters

Nullable properties become filters automatically unless excluded.

| Spec property | Generated predicate |
|---|---|
| `string? Name` | `x => x.Name != null && x.Name.Contains(Name)` |
| `bool? IsActive` | `x => x.IsActive == IsActive.Value` |
| `int? CategoryId` | `x => x.CategoryId == CategoryId.Value` |
| `decimal? MinPrice` | `x => x.Price >= MinPrice.Value` |
| `decimal? MaxPrice` | `x => x.Price <= MaxPrice.Value` |
| `DateTime? CreatedFrom` | `x => x.Created >= CreatedFrom.Value` |
| `DateTime? CreatedTo` | `x => x.Created <= CreatedTo.Value` |

Prefix/suffix conventions:

- `Min...` → `>=`
- `Max...` → `<=`
- `...From` → `>=`
- `...To` → `<=`

---

## Sorting

When the spec contains a `[QuerySort]` string property and a `bool` property named `SortDescending` or `IsDescending`, AutoQuery emits switch-based sorting.

```csharp
[QuerySpec(typeof(Product))]
public partial class ProductQuery
{
    public string? Name { get; set; }
    public decimal? Price { get; set; }
    [QuerySort] public string? SortBy { get; set; }
    public bool SortDescending { get; set; }
}
```

Generated shape:

```csharp
if (SortBy is not null)
{
    query = (SortBy, SortDescending) switch
    {
        ("Name", false) => query.OrderBy(x => x.Name),
        ("Name", true)  => query.OrderByDescending(x => x.Name),
        ("Price", false) => query.OrderBy(x => x.Price),
        ("Price", true)  => query.OrderByDescending(x => x.Price),
        _ => query
    };
}
```

---

## Pagination

When `PageNumber` and `PageSize` are present (or `[QueryPage]` annotated equivalents are detected), AutoQuery emits:

```csharp
query = query.Skip((PageNumber - 1) * PageSize).Take(PageSize);
```

Example:

```csharp
[QuerySpec(typeof(Product))]
public partial class ProductQuery
{
    public string? Name { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
```

---

## HTTP query-string binding

For every `[QuerySpec]` class with writable supported properties, AutoQuery emits:

```csharp
public void BindFromQuery(IEnumerable<KeyValuePair<string, string?>> query);
public void BindFromQuery<TValue>(IEnumerable<KeyValuePair<string, TValue>> query)
    where TValue : IEnumerable<string>;

public static ProductQuery FromQuery(IEnumerable<KeyValuePair<string, string?>> query);
public static ProductQuery FromQuery<TValue>(IEnumerable<KeyValuePair<string, TValue>> query)
    where TValue : IEnumerable<string>;
```

Supported property conversions:

- `string`
- numeric types and nullable numeric types
- `bool` / `bool?`
- `DateTime` / `DateTime?`
- enums and nullable enums

The generic overload is what lets `Request.Query` bind cleanly: `IQueryCollection` enumerates as `KeyValuePair<string, StringValues>`, and `StringValues` implements `IEnumerable<string>`, so no AutoQuery runtime dependency on ASP.NET Core is required.

---

## Diagnostics

| Id | Severity | Description |
|---|---|---|
| `AQ001` | Error | `[QuerySpec]` entity type could not be resolved. |
| `AQ002` | Error | `[QuerySpec]` target class must be declared `partial`. |
| `AQ003` | Warning | Spec has no filterable properties. |

---

## Comparison

| Capability | AutoQuery.Generator | Manual LINQ | Ardalis.Specification |
|---|---|---|---|
| Compile-time generated `Apply` method | ✅ | ❌ | ❌ |
| Compile-time generated query-string binding | ✅ | ❌ | ❌ |
| Reflection-free | ✅ | ✅ | Usually ✅ |
| Convention filters from DTO-like class | ✅ | ❌ | ❌ |
| Custom inline filter expressions | ✅ | Manual only | Via handwritten spec logic |
| Built-in sort switch generation | ✅ | Manual only | Manual only |
| Built-in pagination generation | ✅ | Manual only | Manual only |
| Runtime abstraction dependency | None | None | Package dependency |

---

## Also by the same author

> 🌐 Full suite overview: **[swevo.github.io](https://swevo.github.io/)**


| Package | Description |
|---|---|
| [**AutoWire**](https://github.com/Swevo/AutoWire) | Compile-time DI auto-registration — `[Scoped]`/`[Singleton]`/`[Transient]` generates `IServiceCollection` code. Zero reflection. |
| [**AutoMap.Generator**](https://github.com/Swevo/AutoMap.Generator) | Compile-time object mapping — `[Map(typeof(Dto))]` generates `ToDto()` extension methods. Zero reflection, AOT-safe. |
| [**AutoValidate.Generator**](https://github.com/Swevo/AutoValidate.Generator) | Compile-time FluentValidation wiring — discovers `AbstractValidator<T>` subclasses and generates `AddValidators()`. |
| [**AutoResult.Generator**](https://github.com/Swevo/AutoResult.Generator) | Compile-time `Result<T>` monad — `[TryWrap]` generates `Try*()` wrappers for sync, async and void methods. |
| [**AutoDispatch.Generator**](https://github.com/Swevo/AutoDispatch.Generator) | Compile-time CQRS dispatcher — `[Handler]` generates a strongly-typed `IDispatcher`. No `IRequest<T>`, no reflection. |
| [**AutoLog.Generator**](https://github.com/Swevo/AutoLog.Generator) | Compile-time high-performance logging — `[Log(Level, Message)]` on a partial method generates `LoggerMessage.Define`. AOT-safe. |
| [**AutoHttpClient.Generator**](https://github.com/Swevo/AutoHttpClient.Generator) | Compile-time typed HTTP client — `[HttpClient]` on an interface generates a strongly-typed client. AOT-safe Refit alternative. |

---

## Related Packages

| Package | Downloads | Description |
|---|---|---|
| [AutoWire](https://www.nuget.org/packages/AutoWire) | [![Downloads](https://img.shields.io/nuget/dt/AutoWire.svg)](https://www.nuget.org/packages/AutoWire) | Compile-time dependency injection auto-registration for  |
| [AutoMap.Generator](https://www.nuget.org/packages/AutoMap.Generator) | [![Downloads](https://img.shields.io/nuget/dt/AutoMap.Generator.svg)](https://www.nuget.org/packages/AutoMap.Generator) | Compile-time object mapping for  |
| [AutoArchitecture](https://www.nuget.org/packages/AutoArchitecture) | [![Downloads](https://img.shields.io/nuget/dt/AutoArchitecture.svg)](https://www.nuget.org/packages/AutoArchitecture) | Compile-time architecture/dependency-rule enforcement for  |
| [AutoHttpClient.Generator](https://www.nuget.org/packages/AutoHttpClient.Generator) | [![Downloads](https://img.shields.io/nuget/dt/AutoHttpClient.Generator.svg)](https://www.nuget.org/packages/AutoHttpClient.Generator) | Compile-time typed HTTP client generation for  |
| [AutoDispatch.Generator](https://www.nuget.org/packages/AutoDispatch.Generator) | [![Downloads](https://img.shields.io/nuget/dt/AutoDispatch.Generator.svg)](https://www.nuget.org/packages/AutoDispatch.Generator) | Compile-time CQRS dispatcher for  |
| [AutoLog.Generator](https://www.nuget.org/packages/AutoLog.Generator) | [![Downloads](https://img.shields.io/nuget/dt/AutoLog.Generator.svg)](https://www.nuget.org/packages/AutoLog.Generator) | Compile-time high-performance logging for  |
| [AutoValidate.Generator](https://www.nuget.org/packages/AutoValidate.Generator) | [![Downloads](https://img.shields.io/nuget/dt/AutoValidate.Generator.svg)](https://www.nuget.org/packages/AutoValidate.Generator) | Compile-time FluentValidation wiring for  |

---

## License

MIT.
