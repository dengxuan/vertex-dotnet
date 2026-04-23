# Contributing to vertex-dotnet

Thanks for your interest!

This repo is the **.NET implementation** of Vertex. The spec lives at [dengxuan/Vertex](https://github.com/dengxuan/Vertex); please read carefully before proposing changes that touch the wire format.

## Types of changes

### A. Pure .NET change (bug fix, refactor, perf, new test)

Single PR here. Standard review. No cross-repo coordination needed.

### B. Change that affects the wire format

You must also open a companion PR in the [Vertex spec repo](https://github.com/dengxuan/Vertex). Process:

1. Open an issue in the Vertex spec repo first, tag `spec`.
2. In the spec repo, update `/spec/wire-format.md` (or related) in one PR.
3. In this repo, implement the conformant change in a companion PR. Link the spec PR.
4. Both PRs must be reviewed; the spec PR merges last (after impl PRs on all impacted language repos are approved and green).
5. `/compat/` tests in the spec repo run the multi-language matrix.

### C. Shared `.proto` change

Follow the process in the Vertex spec repo's CONTRIBUTING.md § B. This repo regenerates its proto code after the shared protos update.

## Branch / PR conventions

- [Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `docs:`, `chore:`, `refactor:`, `test:`, `perf:`, `ci:`.
- PR title = commit title (we squash-merge single-commit PRs).
- PR description: what changed, why, test plan.

## Versioning

MinVer + git tags on `main`, matching the [Skywalker convention](https://github.com/dengxuan/Skywalker/blob/main/docs/versioning.md):

- tag `vX.Y.Z` → publishes `Vertex.*.X.Y.Z` NuGet packages (GA)
- tag `vX.Y.Z-rc.N` / `-preview.N` → pre-release packages
- main branch push (no tag) → `X.Y.(Z+1)-alpha.0.N` via MinVer auto-increment

The **wire spec** is versioned independently from this package. Multiple NuGet versions can implement the same wire version.

## Local development

```bash
dotnet restore Vertex.sln
dotnet build
dotnet test
```

### Regenerating protos from the spec repo

```bash
# (script lands with the first migration commit)
./scripts/sync-protos.sh
```

## Testing against other implementations

```bash
# from the Vertex spec repo
cd ../Vertex/compat
make fetch-impls
make run-all
```

## Where else to go

- **Wire-format questions** — [Vertex spec repo issues](https://github.com/dengxuan/Vertex/issues)
- **Go implementation** — [vertex-go](https://github.com/dengxuan/vertex-go)
- **Integration with Skywalker** — [Skywalker repo](https://github.com/dengxuan/Skywalker)
