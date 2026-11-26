#!/usr/bin/env python3
"""Print the test projects a branch's changes can affect, one path per line.

`ALL` means run the whole suite: the change reaches the build itself or a file no project owns.
No output means nothing testable changed. Usage: affected_tests.py [base], base defaulting to
origin/master; compares the merge base with the working tree, untracked files included.
"""
import re
import subprocess
import sys
from pathlib import Path, PurePosixPath

# Never run by pull request CI, so never a local gate either.
SKIPPED = {"tests/Svg.Skia.Benchmarks/Svg.Skia.Benchmarks.csproj",
           "tests/Svg.Studio.UiTests/Svg.Studio.UiTests.csproj"}

# Paths no project compiles, so changing them can only affect a test through these readers.
DOC_PREFIXES = ("site/", "docs/", "plan/", "images/", "icon/", ".github/", ".claude/")

# Tests that open repository files at runtime, which no project reference shows.
RUNTIME_READERS = {
    "src/": "tests/Svg.Model.UnitTests/Svg.Model.UnitTests.csproj",  # Architecture/ProjectBoundaryGuardTests
    "samples/SvgRecipeDemo/": "tests/Svg.Expressions.Recipes.UnitTests/Svg.Expressions.Recipes.UnitTests.csproj",
}


def git(root, *args):
    return subprocess.run(["git", "-C", str(root), *args], check=True,
                          capture_output=True, text=True).stdout.split("\n")


def rel(root, path):
    return PurePosixPath(path.resolve().relative_to(root)).as_posix()


def main():
    root = Path(git(Path.cwd(), "rev-parse", "--show-toplevel")[0])
    base = sys.argv[1] if len(sys.argv) > 1 else "origin/master"
    merge_base = git(root, "merge-base", base, "HEAD")[0]
    changed = {f for f in git(root, "diff", "--name-only", merge_base)
               + git(root, "ls-files", "--others", "--exclude-standard") if f}

    projects = [p for p in root.glob("**/*.csproj")
                if not {"bin", "obj", "externals"} & set(p.relative_to(root).parts)]
    references = {}   # project -> projects it references
    linked = {}       # file outside a project's folder -> projects that include it
    for project in projects:
        name = rel(root, project)
        references[name] = set()
        for element, value in re.findall(r'<(\w+)\s+(?:Include|Project)="([^"]*\.\.[^"]*)"', project.read_text()):
            target = project.parent / value.replace("\\", "/")
            if element == "ProjectReference":
                references[name].add(rel(root, target))
                continue
            for match in target.parent.glob(target.name) if "*" in target.name else [target]:
                if match.exists():
                    linked.setdefault(rel(root, match), set()).add(name)

    seeds = set()
    for file in sorted(changed):
        owners, directory = set(), (root / file).parent
        while directory != root and not owners:
            owners = {rel(root, p) for p in directory.glob("*.csproj")}
            directory = directory.parent
        owners |= linked.get(file, set())
        owners |= {test for prefix, test in RUNTIME_READERS.items() if file.startswith(prefix)}
        if owners:
            seeds |= owners
        elif not (file.startswith(DOC_PREFIXES) or ("/" not in file and file.endswith(".md"))):
            print("ALL")  # build props, global.json, the solution, a submodule pointer, ...
            return

    dependents = {}
    for project, targets in references.items():
        for target in targets:
            dependents.setdefault(target, set()).add(project)
    reached, pending = set(seeds), list(seeds)
    while pending:
        for dependent in dependents.get(pending.pop(), ()):
            if dependent not in reached:
                reached.add(dependent)
                pending.append(dependent)

    solution = (root / "Svg.Skia.slnx").read_text()
    tests = set(re.findall(r'Path="(tests/[^"]+\.csproj)"', solution)) - SKIPPED
    print("\n".join(sorted(reached & tests)))


if __name__ == "__main__":
    main()
