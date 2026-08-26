---
name: chrome-references
description: Capture or refresh the Chrome reference images the Svg.Skia render suites diff against — W3C SVG 1.1, resvg, and WPT SVG 2. Use when a baseline needs regenerating, when adding a Chrome override for a fixture, or when inspecting a fixture in a browser to work out why a row fails.
---

# Chrome reference captures

Three render suites diff against PNGs captured from **real Google Chrome** rather than against a
corpus's own reference images. The W3C suite's shipped PNGs are old and do not always match how any
browser renders today, so where a row is visually right but pixel-wrong against the legacy image, the
comparison is repointed at a Chrome capture instead.

| Corpus | Fixtures | Captures | Script |
| --- | --- | --- | --- |
| W3C SVG 1.1 | 525 in `externals/W3C_SVG_11_TestSuite/W3C_SVG_11_TestSuite/svg` | 441 in `tests/Svg.Skia.UnitTests/ChromeReference/W3C` | `scripts/capture_w3c_chrome_overrides.mjs` |
| resvg | 1715 in `externals/resvg/crates/resvg/tests/tests` | 124 in `.../ChromeReference/resvg` | `scripts/capture_resvg_chrome_overrides.mjs` |
| WPT SVG 2 | 83 in `externals/WPT_SVG_2/svg` | 46 in `.../ChromeReference/WPT/svg` | `scripts/capture_wpt_svg2_chrome_references.mjs` |

## Running a capture

```sh
node scripts/capture_w3c_chrome_overrides.mjs masking-path-04-b,linking-a-09-b
```

Names are comma-separated, and a trailing `.png` is stripped for you. The WPT script takes paths
relative to `svg/` instead — `shapes/circle-01` — and adds the extension itself.

**Passing no names does not mean "capture everything".** For W3C and resvg it re-captures exactly the
PNGs already sitting in the output directory, so a bare run refreshes the existing set and adds
nothing. Creating a *new* override means naming the fixture explicitly. The WPT script is the
exception: with no arguments it walks the whole corpus.

Each script starts a local HTTP server on `127.0.0.1` on an ephemeral port, writes an HTML wrapper
under `output/playwright/`, and shells out to `npx playwright screenshot --channel chrome`. The
`chrome` channel is real Chrome, not Playwright's bundled Chromium, so Chrome must be installed.

Viewports differ, and they are not arbitrary — each matches what its suite asks the renderer for:

- **W3C** is fixed at `480x360`, the standalone viewport policy `W3CTestSuiteTests` uses.
- **resvg** reads the fixture's own `width`/`height` or `viewBox` (default `200x200`) and scales by 1.5.
- **WPT** reads the same (default `300x150`) at scale 1.

## Never use `file://`

Chrome treats every `file:` URL as a unique security origin. A fixture that loads a linked resource,
a font, a nested document or an iframe fails with:

```
Unsafe attempt to load URL file:///... 'file:' URLs are treated as unique security origins.
```

This applies to inspecting a fixture by hand as much as to capturing one. If you hit that warning,
rerun over HTTP rather than working around it in place — serve the repo root and open

```
http://127.0.0.1:<port>/externals/W3C_SVG_11_TestSuite/W3C_SVG_11_TestSuite/svg/<name>.svg
```

When debugging with a harness page or an iframe, keep the parent page and the target SVG on the
**same** origin. Do not mix an HTTP harness with a `file://` fixture, or the reverse.

## Rules about the baselines themselves

- Overrides must come from a real Chrome capture. Never copy one out of the legacy W3C PNG set.
- Where an override exists, keep the test pointed at it rather than at the W3C reference PNG.
- A fixture that needs JavaScript, DOM APIs or browser-only behaviour the library does not implement
  stays **skipped with a reason**. Do not manufacture a baseline for it — `W3CTestSuiteTests` lists
  those by name in `s_javaScriptW3CTests`.
- Do not reintroduce footer exclusion regions. Prefer a Chrome override plus a narrowly-scoped
  per-fixture threshold where the library is visually aligned but differs at the raster level.
- A hairline failure after a SkiaSharp bump usually wants a threshold nudge, not a re-capture. The
  W3C text rows are calibrated against a particular native Skia.

## Verifying

Run the focused rows before the full suite:

```sh
dotnet test tests/Svg.Skia.UnitTests/Svg.Skia.UnitTests.csproj -c Release \
  -f net10.0 --no-restore --filter "FullyQualifiedName~W3CTestSuiteTests.Tests"
```

Then the subset that changed, then everything. `output/playwright/` is scratch — it holds the
generated wrappers and does not belong in a commit.
