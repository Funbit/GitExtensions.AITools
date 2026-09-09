# Release Checklist

## Before tagging

- [ ] Update `VersionPrefix` in `GitExtensions.AITools.csproj` (e.g. `7.0.0`)
- [ ] Rename `## [Unreleased]` to `## [x.y.z] - YYYY-MM-DD` in `CHANGELOG.md`
- [ ] Add a fresh `## [Unreleased]` section above the new entry
- [ ] Verify the changelog entry covers all user-facing changes
- [ ] Verify `GitExtensionsReferenceVersion` is the Git Extensions release used for testing (currently `v7.2.1`)
- [ ] Verify `GitExtensionsExtensibilityVersion` in the csproj matches the targeted API line (currently `7.0.0`, published as `7.0.0.135`); the dependency range `[7.0.0, 8.0.0)` is computed automatically
- [ ] `dotnet build -c Release` — no errors
- [ ] `dotnet pack -c Release` — inspect .nupkg contents look correct
- [ ] Commit: "release: vX.Y.Z"
- [ ] Merge to `main`

## Tag & publish

- [ ] `git tag vX.Y.Z` on the merge commit
- [ ] `git push origin vX.Y.Z` — CI builds, packs, and publishes to NuGet
- [ ] Verify the package appears on nuget.org with correct metadata and release notes

## After publishing

- [ ] Verify the GitHub Release was auto-created with the correct changelog body
- [ ] If this is a new GE major version: create maintenance branch for the old version (e.g. `ge6.x`)
