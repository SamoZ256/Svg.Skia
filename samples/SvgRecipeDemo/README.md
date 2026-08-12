# SvgRecipeDemo

The [recipe](../../SVG_EXPRESSIONS.md#9-converting-an-existing-drawing) workflow as a live
editor. Edit the recipe on the left; the converted SVG, the generated C# and the drawing all
follow.

```sh
dotnet run --project samples/SvgRecipeDemo
```

```
┌ Recipe ──────────────┬ Converted SVG ───────┬ Rendering ───────────┐
│  <param name="hue"…  │  <e:param name="hue" │                      │
│  <let name="body">…  │  …                   │        🏠            │
│  <replace color=     │  fill="{{ body }}"   │                      │
│    "#000000">body    │                      ├──────────────────────┤
├ Premade SVG ─────────┼ Generated C# ────────┤  #000000 → {{ body }}│
│  <path … fill=       │  Record(float hue …  │  hue   ──○────  0.580│
│    "#000000" />      │  SvgHsl(tone, …)     │  warm  ○──────  0.000│
│                      │                      │  dark  ☐             │
└──────────────────────┴──────────────────────┴──────────────────────┘
```

Four stages run on every edit:

1. **`SvgRecipeRewriter`** applies the recipe to the premade SVG.
2. **`SkiaCSharpCodeGen`** turns the converted document into C#.
3. **Roslyn** compiles that into a collectible `AssemblyLoadContext`.
4. **`Record(…)`** is invoked by reflection with the current slider values.

Every stage is the shipping code, so there is no second implementation here that could drift from
what `svgrecipe` and `svgc` produce. Edits are debounced and run off the UI thread; a failed edit
keeps the last good assembly loaded, so the view does not blank while typing. Diagnostics from all
three stages — recipe, expression type checker and C# compiler — surface in one panel, and each
`<replace>` rule reports what it claimed.

The **Premade SVG** pane is editable too, so another icon can be pasted in without rebuilding.

Number parameters are exposed on a 0..1 slider, starting at the declared default when that is a
plain literal. Scale inside the expression when a wider range is wanted, as `demo.recipe` does
with `hue * 360`.

`--render <dir>` runs the same chain without a display and writes a frame per hue, which is how to
check a change on a machine with no window server:

```sh
dotnet run --project samples/SvgRecipeDemo -- --render frames
```

## Files

| | |
|---|---|
| `Svg/demo.svg` | The premade drawing — a house icon from [Streamline](https://streamlinehq.com), unmodified. It paints one colour, `#000000`. |
| `Recipe/demo.recipe` | The recipe: three parameters, two lets, one `<replace>` rule. |

## Things to try

- Move `hue`. One `<replace>` rule drives the whole icon.
- Tick `dark`. `body` reads `dark ? 34% : 58%`, and the conditional is in the generated C#.
- Change `#000000` in the recipe to something the drawing does not use. The rule reports that it
  matched nothing, and the icon goes back to black — the conversion is a no-op, not an error.
- Add `<param name="tint" type="color" />` and use it in `body` via
  `mix(hsl(tone, 72%, 58%), tint, 0.4)`. A colour parameter gets a text box rather than a slider,
  and cannot carry a `default` — see [§1.2](../../SVG_EXPRESSIONS.md#12-the-declaration-block).
  Declare it before the parameters that do have defaults.
- Put an expression somewhere unsupported, `<replace>` on a colour inside a `stroke-width`, and
  watch nothing happen — [§7](../../SVG_EXPRESSIONS.md#7-limitations) explains why unsupported
  attributes fail silently.
