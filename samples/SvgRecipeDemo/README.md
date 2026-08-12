# SvgRecipeDemo

The [recipe](../../SVG_EXPRESSIONS.md#9-converting-an-existing-drawing) workflow as a live
editor. Edit the recipe on the left; the converted SVG, the generated C# and the drawing all
follow.

```sh
dotnet run --project samples/SvgRecipeDemo
```

```
┌ Recipe ──────────────┬ Converted SVG ───────┬ Rendering ───────────┐
│  <param name=        │  <e:param name=      │                      │
│   "blackColor" …     │   "blackColor" …     │        🏠            │
│  <let name=          │  …                   │                      │
│   "blackColor70">…   │  fill=               ├──────────────────────┤
│  <replace color=     │   "{{ stateBlack…"   │  #000000 →           │
│    "#000000">…       │                      │   {{ stateBlackCol…}}│
├ Premade SVG ─────────┼ Generated C# ────────┤  accentColor #7c3aed │
│  <path … fill=       │  Record(SKColor      │  blackColor  #000000 │
│    "#000000" />      │   accentColor, …)    │  whiteColor  #ffffff │
│                      │  SvgWithAlpha(…)     │  state   ☐  accent ☐ │
│                      │                      │  enabled ☐  isLight☐ │
└──────────────────────┴──────────────────────┴──────────────────────┘
```

Four stages run on every edit:

1. **`SvgRecipeRewriter`** applies the recipe to the premade SVG.
2. **`SkiaCSharpCodeGen`** turns the converted document into C#.
3. **Roslyn** compiles that into a collectible `AssemblyLoadContext`.
4. **`Record(…)`** is invoked by reflection with the current control values.

Every stage is the shipping code, so there is no second implementation here that could drift from
what `svgrecipe` and `svgc` produce. Edits are debounced and run off the UI thread; a failed edit
keeps the last good assembly loaded, so the view does not blank while typing. Diagnostics from all
three stages — recipe, expression type checker and C# compiler — surface in one panel, and each
`<replace>` rule reports what it claimed.

The **Premade SVG** pane is editable too, so another icon can be pasted in without rebuilding.

Number parameters are exposed on a 0..1 slider, starting at the declared default when that is a
plain literal; scale inside the expression when a wider range is wanted. Booleans get a check box
and colours a text box, since a colour has no meaningful range to sweep.

`--render <dir>` runs the same chain without a display, sweeping a number parameter over its range
or — as with this recipe, which has none — writing every boolean both ways. That is how to check a
change on a machine with no window server:

```sh
dotnet run --project samples/SvgRecipeDemo -- --render frames
```

## Files

| | |
|---|---|
| `Svg/demo.svg` | The premade drawing — a house icon from [Streamline](https://streamlinehq.com), unmodified. It paints one colour, `#000000`. |
| `Recipe/demo.recipe` | The recipe: seven parameters, five lets, one `<replace>` rule. |

The recipe is a port of a PaintCode-generated overlay — three colour parameters, four booleans,
and four `withAlpha` lets feeding a nested conditional:

```
enabled and state ? (accent ? accentColor : (isLight ? whiteColor70 : blackColor70))
                  : (isLight ? whiteColor30 : blackColor30)
```

No parameter declares a `default`, so every one is required. Colour parameters could not have one
anyway — a colour is not a C# compile-time constant
([§1.2](../../SVG_EXPRESSIONS.md#12-the-declaration-block)).

## Things to try

- Tick `enabled` **and** `state`. Only together do they reach the first branch, which is where
  `accent` and the 70% alphas live. Either one alone leaves the drawing on the 30% branch.
- With both ticked, tick `accent`. `accentColor` replaces the black/white pair outright — an
  expression selecting a whole parameter rather than shading one.
- Tick `isLight` on its own. It is the one boolean that changes the drawing from the default
  state, since it is read on both sides of the outer conditional.
- Change `#000000` in the recipe to something the drawing does not use. The rule reports that it
  matched nothing, and the icon goes back to black — the conversion is a no-op, not an error.
- Give `whiteColor` a `default`. It is rejected, with the reason.

`--render` toggles one boolean at a time from `false`, so only the `isLight` pair of frames
differs — `enabled and state` is never reached by a sweep that changes a single parameter. The
combinations are worth exploring in the window.
- Put an expression somewhere unsupported, `<replace>` on a colour inside a `stroke-width`, and
  watch nothing happen — [§7](../../SVG_EXPRESSIONS.md#7-limitations) explains why unsupported
  attributes fail silently.
