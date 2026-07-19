# Releasing Slnmap

Slnmap ships as a .NET global tool (`slnmap`) on NuGet. Releases are **tag-driven**.

## Version bump strategy

The **git tag is the single source of truth** for the released version. `src/Slnmap.Cli/Slnmap.Cli.csproj`
carries a development default (`<Version>0.1.0</Version>`) used only for local `dotnet run`/`pack`; the
CI release workflow overrides it from the tag.

Versions follow [SemVer](https://semver.org): `MAJOR.MINOR.PATCH`.
- **PATCH** — bug fixes, no behavior change to tools or graph schema.
- **MINOR** — new tools/flags, additive schema changes (readers of an older graph still work).
- **MAJOR** — breaking CLI/tool contract or a graph-schema change requiring re-analysis (bump
  `SqliteSchema.Version` in the same release).

## Cutting a release

1. Ensure `main` is green (`dotnet build` + `dotnet test`) and `bench/RESULTS.md` / README are current.
2. Tag and push:
   ```console
   git tag v0.2.0
   git push origin v0.2.0
   ```
3. The `Release` workflow (`.github/workflows/release.yml`) restores, builds and tests at that version,
   packs `Slnmap.Cli` with `-p:Version=0.2.0`, and pushes the `.nupkg` to NuGet using the
   `NUGET_API_KEY` repository secret (`--skip-duplicate`, so re-running a tag is safe).

## Prerequisite (one-time)

Add a NuGet API key as the repository secret **`NUGET_API_KEY`**
(Settings → Secrets and variables → Actions).

## Verify a package locally before tagging (optional)

```console
dotnet pack src/Slnmap.Cli/Slnmap.Cli.csproj -c Release -o ./feed
dotnet tool install --global --add-source ./feed Slnmap
slnmap doctor
dotnet tool uninstall --global Slnmap
```
