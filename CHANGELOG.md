# Changelog

All notable changes to this project will be documented in this file.

## [1.1.0] - 2026-07-10

### Added
- Generated `BindFromQuery(...)` and `FromQuery(...)` helpers on `[QuerySpec]` partial classes.
- Compile-time query-string binding for `string`, numeric types, `bool`, `DateTime`, enums, and nullable variants.
- Generic query-binding overloads that work with ASP.NET Core `Request.Query` via `IEnumerable<KeyValuePair<string, TValue>>` without adding an ASP.NET Core package dependency.
- Tests covering generated query binding, malformed input handling, and end-to-end `.Apply()` integration.

## [1.0.0] - 2026-06-25

### Added
- Initial `AutoQuery.Generator` release.
- Incremental Roslyn source generator for query specs.
- Convention-based filters for nullable value types and strings.
- Custom `[QueryFilter]`, `[QueryIgnore]`, `[QuerySort]`, and `[QueryPage]` support.
- Diagnostics `AQ001`, `AQ002`, and `AQ003`.
- xUnit test suite covering generation and diagnostics.
- GitHub Actions CI, package metadata, and GitHub Pages landing page.
