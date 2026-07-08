# Structural union classifier — ASP.NET Core verification

Scratch experiment (NOT product code) that answers three questions from a sibling
session about `System.Text.Json.Serialization.JsonStructuralUnionTypeClassifier`
(dotnet/runtime prototype at commit `1056645ef900a2eaa5be47c5e5578cb6ceb65637`).

## What it does

Single-file WebApplication + STJ harness. Q1 spins up Kestrel on 127.0.0.1:5099
and posts JSON via in-process HttpClient. Q2/Q3 hit `JsonSerializer.Deserialize`
directly.

## Prerequisites

- .NET SDK `11.0.100-preview.7.26355.102` (pinned in this folder's `global.json`).
- Prototype `System.Text.Json.dll` at
  `D:\code\scratch-runtime\artifacts\bin\System.Text.Json\Release\net11.0\System.Text.Json.dll`.

The `App.csproj` references that dll as `<Private>true</Private>` so it becomes
app-local and wins over the shared framework's STJ.

## Build & run

```powershell
# From OUTSIDE the aspnetcore repo (to escape its Directory.Build.props / global.json).
Copy-Item <this folder>\* <scratch>
cd <scratch>
dotnet build -c Release
dotnet run  -c Release --no-build
```

Output is echoed to stdout and written to `run-report.txt`.

## Findings summary

- **Q1** (ASP.NET Core wiring via `ConfigureHttpJsonOptions`): ✅ works.
- **Q2** (precedence): `[JsonUnion(TypeClassifier=…)]` on the type **wins**
  over globally-registered `JsonSerializerOptions.TypeClassifiers`. Source
  pinpoint: `JsonTypeInfo.cs` lines 1136–1146 — options list is consulted
  only in the `else` branch when the per-type factory is null.
- **Q3** (`{"Name":"x","Breed":"y"}` on `union UnionPet(Cat, Dog)`):
  disambiguable via `[JsonUnmappedMemberHandling(Disallow)]` or a `required`
  discriminator property. Both work reliably, both are non-obvious for a
  typical caller — needs docs.

See `run-report.txt` for the full per-test table.
