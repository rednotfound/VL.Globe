# Releasing VL.Globe

The steps to the first release on nuget.org, what each proves, and what is the maintainer's to decide.
Written 2026-11-28, modelled on VL.Mapsui's `docs\RELEASE.md` (the fuller account of the publishing
methods is VL.NetTopologySuite's).

**Nothing here is done automatically, and nothing is pushed without the maintainer saying so.** A version
on nuget.org can be unlisted, never deleted or replaced. The id `VL.Globe` is free on nuget.org (checked
2026-11-28): the first upload creates it.

## Published

**0.1.0-alpha, 2026-11-28** - uploaded in the browser by the maintainer; installed back from nuget.org alone the
same evening (`Test-Install.ps1 -Published -Version 0.1.0-alpha`: signed, the four dependencies, all 16 help
patches compile; the installed dll and README are byte for byte those of `b5adfe5`). Tag `v0.1.0-alpha` and the
GitHub release: pending the maintainer's go. The tree is at the working version `0.1.1-alpha`.

## Where it stood before the upload (2026-11-28)

| | |
|---|---|
| version in the tree | `0.1.0-alpha` (nuspec, the tile requests' User-Agent) |
| branch | `main` (renamed from master, as the siblings') - `feature/camera-relative` fast-forwarded into it and pushed, 2026-11-28 |
| nodes | 33, each with an F1 help patch; 16 help patches in six topics; every layer that fetches is off until switched on |
| tests | 381 pass, 9 probes skipped |
| verified on | vvvv gamma 7.4 (LTS); not tried on 8.0 previews |
| depends on | NetTopologySuite 2.6.0, NetTopologySuite.Features 2.1.0, BruTile 5.0.6, VL.NetTopologySuite 0.0.10-alpha (all on nuget.org) |
| packed beside the dll | the help, the shaders, `help\Assets\liberty-3d.json`, README, LICENSE, THIRD-PARTY-NOTICES, ARCHITECTURE |

## Decided for this release

- **A prerelease, `-alpha`**, as every package of the family (the maintainer's rule, 2026-09-25). The
  number is the roadmap's 0.1 (VL.Mapsui's basics on a globe, plus vector tiles), not the family's 0.0.x
  run - **to confirm: `0.1.0-alpha`, or plain `0.1.0`**.
- The widgets (ScaleBar, ZoomButtons, Click) are not in it: "小部件不是很想做。先不做了" (2026-11-28).
- The style shipped with the examples derives from OSM Liberty (BSD 3-Clause code, CC BY 4.0 design)
  through OpenFreeMap's styles (MIT): the texts are in THIRD-PARTY-NOTICES.md, packed.

## Decided 2026-11-28, after the list below was put to the maintainer

They asked for a search on the Enabled default and left the rest to this side ("其他的你帮我也做选择"):

- **Every layer that fetches is off by default** (3 below): `OpenStreetMap` and `XyzTiles` as `VectorTiles`;
  windows stay on. vvvv's own network and device nodes are off by default, its windows and renderers on
  (docs/DESIGN-NODES.md, section 6); the maintainer expected it so. Five help patches whose OpenStreetMap
  had no toggle - and fetched when opened - got one; `Stack tile layers`' toggle drives its layers now,
  not its window. **VL.Mapsui's tile layers are still on by default (published): to bring in line there.**
- **`0.1.0-alpha`** (1): the family's rule stands; the string can still be changed before the upload.
- **Released although the surface moved this week** (2): a first release has no patches to break, and it
  says the nodes may change.
- **The three CARTO rows are removed** (4) from the URLs and the names.
- **The overview's `GlobeWindow` node stays** (5): with the layers off it opens a plain blue globe and
  fetches nothing.

## Open before the upload - the maintainer's

1. **The version string** (above).
2. **The family rule "the node surface has not moved for a week" is not met**: TileCache and the `Cache`
   pins are of 2026-11-28, the layers' single Input of 2026-11-23. A first release may move anyway - it
   says "the nodes may still change" - but it is a rule we would be setting aside.
3. **`VectorTiles`' Enabled is off by default** while OpenStreetMap, XyzTiles and the windows are on.
   Changing it after a release changes what a saved patch does.
4. **`Layers\HowTo Choose a vector tile style` lists three CARTO styles** among its URLs. The family's rules
   record (2026-08-17) that CARTO's basemaps return a good tile and do not permit this use. Keep, or
   remove the three rows.
5. **The overview's `GlobeWindow` node opens an empty globe window** when the Help Browser opens the
   Explanation. Keep, or replace the node with a line of text.
6. `docs\` has no screenshot; the README has none either (the siblings' have none).
7. Not yet seen by the maintainer in vvvv: the drag near the poles, the attribution line, the ground halo,
   the three help patches made on 2026-11-28 after their arranging.

## The steps, in order

Every step separate; a validator never runs in the same command as a commit or a push.

```powershell
# 0. vvvv closed, tree clean.
git status

# 1. The gate, from a clean build (2026-11-28: all green, see below):
.\build.ps1
dotnet test test\VL.Globe.Tests\VL.Globe.Tests.csproj
.\pack.ps1 -NoBuild
.\tools\Test-VLPackage.ps1
.\tools\Test-VLPatch.ps1
.\tools\Compile-HelpPatches.ps1 -OutputDirectory <an absolute folder>
.\tools\Test-Install.ps1 -FromNuGetOrg -OutputDirectory <an empty folder>

# 2. The release commit on main, pushed (done 2026-11-28 on the maintainer's word).

# 3. The maintainer uploads dist\feed\VL.Globe.0.1.0-alpha.nupkg at nuget.org (Upload -> Verify:
#    four dependencies incl. VL.NetTopologySuite 0.0.10-alpha, the README preview -> Submit).

# 4. After the confirmation email (indexing: minutes, up to an hour), install back from nuget.org alone:
.\tools\Test-Install.ps1 -Published -Version 0.1.0-alpha
#    then in vvvv: nuget install VL.Globe -pre, Help Browser -> VL.Globe ->
#    HowTo Open a globe window, switch Enabled on.

# 5. Tag the commit the package was packed from, and push the tag.
git tag v0.1.0-alpha
git push origin v0.1.0-alpha

# 6. GitHub release on the tag (gh release create, body = the nuspec's release notes + the install line).

# 7. The same day: nuspec <version> and TileSourceFactory.UserAgent -> the next working version, the
#    release notes gain "x - In development, not published."; build, test, commit.
```

Lessons carried from the siblings' releases: pack AFTER the last edit of the release notes and the README,
and look inside the nupkg before tagging; an update on the same machine within 30 minutes of a publish
needs `-version` (NuGet caches the version list); a failed first run of the install test in the export
step has been a one-off - rerun before suspecting the package.
