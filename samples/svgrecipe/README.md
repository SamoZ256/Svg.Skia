# svgrecipe

Rewrites a plain SVG into the [expression extension format](../../SVG_EXPRESSIONS.md), driven by
a recipe file that says which colours become which expressions.

```sh
dotnet run --project samples/svgrecipe -- \
  -i samples/svgrecipe/Example/badge.svg \
  -r samples/svgrecipe/Example/badge.recipe \
  -o badge.expr.svg
```

```
Converting: badge.expr.svg
  #3b82f6 -> {{ primary }} (3)
  rgb(30,64,175) -> {{ deep }} (2)
  red -> {{ alert }} (2)
```

The output is an ordinary SVG document, so it goes straight into `svgc` or into the source
generator:

```sh
dotnet run --project samples/svgc -- -i badge.expr.svg -o Badge.cs -n Demo -c Badge
```

| Option | |
|---|---|
| `-i`, `--inputFile` | The plain SVG to convert. |
| `-r`, `--recipeFile` | The recipe describing the conversion. |
| `-o`, `--outputFile` | Where to write the converted SVG. |
| `-q`, `--quiet` | Do not report what each rule matched. |

A rule that matched nothing is reported as a warning rather than an error, since one recipe
usually covers a family of drawings and not every drawing uses every colour. Exit status is
non-zero only when the recipe or the document is faulty.

The recipe format, what counts as the same colour, and the limits of the conversion are
documented in [SVG_EXPRESSIONS.md §9](../../SVG_EXPRESSIONS.md#9-converting-an-existing-drawing).
