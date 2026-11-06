# SharedHost shared core

Pure, SDK-free logic for the opt-in `Server.WorkerSharingMode="shared-host"` mode
(scheduler, settings, admission policy, registry v2 record model, catalogs).

Every `*.cs` file in this directory is compiled into both `GxMcp.Gateway` (net10.0)
and `GxMcp.Worker` (net48 x86) through a wildcard link:

```xml
<Compile Include="..\GxMcp.Shared\SharedHost\*.cs" LinkBase="Shared\SharedHost" />
```

Rules for files in this directory:

- Namespace `GxMcp.Common.SharedHost`, `#nullable enable` at the top of each file.
- Reference only the BCL and Newtonsoft.Json. No GeneXus SDK, no ASP.NET Core.
- Use C# syntax that both compilers accept and APIs available on .NET Framework 4.8.
- Keep members `internal`; tests in `GxMcp.Gateway.Tests` reach them through
  `InternalsVisibleTo`.
