# slnmap-ts

Frontend HTTP call-site extractor for [slnmap](https://slnmap.dev)
([source](https://github.com/EMahmoudNabil/slnmap)). Walks a TypeScript/React project with the
TypeScript Compiler API and emits a JSON artifact of resolved and unresolved HTTP call sites.
`slnmap`'s `.NET` CLI (the `analyze-ts` verb) ingests that artifact into the same code-graph
database the rest of slnmap uses — this package never touches SQLite itself.

## Usage

```sh
npx slnmap-ts extract <project-root> --tsconfig <path/to/tsconfig.json> --out <file>.json
```

- `<project-root>` — the frontend project's root directory.
- `--tsconfig` — path to the `tsconfig.json` to load (defaults to `<project-root>/tsconfig.json`).
- `--out` — where to write the JSON artifact.

## Requirements

Node.js 18+. TypeScript is bundled as a pinned dependency — the target project's own installed
TypeScript version (if any) is never used, so `slnmap-ts` behaves identically regardless of what
TypeScript release the analyzed project depends on.

## Known limitations

Validated against a pre-publish field trial across several real and public codebases before
this version shipped. One gap from that trial is still open and worth calling out here directly:

- **Monorepo / TypeScript project-references tsconfigs are untested.** Every project exercised
  so far (in development and in the field trial) uses a single, flat `tsconfig.json`. A
  `references`-based setup (Nx, Turborepo, and similar monorepo tooling) has not been run
  through `slnmap-ts` at all — it may work (the loader uses `ts.parseJsonConfigFileContent`,
  which understands `references`), but this is not yet a verified claim. Treat results on such
  a project as unverified until confirmed.

Plain JavaScript projects (no TypeScript at all) work once a `tsconfig.json` with `"allowJs":
true` exists — this has been verified against a real, unmodified public codebase — but
`slnmap-ts` does not create one for you; see the `analyze-ts` CLI's error message if none is
found.

## Development

```sh
npm install
npm run build   # compiles src/ -> dist/
npm test        # compiles src/+test/ -> dist-test/, runs with node --test
```
