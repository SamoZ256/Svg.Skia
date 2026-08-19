#!/usr/bin/env python3
"""Refuse any gh pull-request write aimed at a repository other than this fork.

This clone is a fork: `origin` is SamoZ256/Svg.Skia, while the upstream it was forked
from is wieslawsoltes/Svg.Skia. On a fork `gh pr create` targets the PARENT by default,
so an unpinned create opens a pull request on somebody else's repository — public, and
awkward to undo. The guard denies that outright rather than relying on anyone
remembering --repo.
"""
import json
import re
import sys

ALLOWED = "SamoZ256/Svg.Skia"

# Subcommands that write. Read-only ones (view, list, status, diff, checks) are left alone.
WRITE_SUBCOMMANDS = r"create|merge|edit|close|reopen|ready|comment|review|lock|unlock"


def deny(reason: str) -> None:
    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "deny",
            "permissionDecisionReason": reason,
        }
    }))
    sys.exit(0)


def main() -> None:
    try:
        payload = json.load(sys.stdin)
    except Exception:
        # A guard that crashes must not block every Bash call; fail open and stay quiet.
        return

    command = (payload.get("tool_input") or {}).get("command") or ""

    if not re.search(rf"\bgh\s+pr\s+({WRITE_SUBCOMMANDS})\b", command):
        return

    match = re.search(r"""(?:--repo|-R)[=\s]+['"]?([^\s'"|;&)]+)""", command)

    if match:
        target = match.group(1)
        target = re.sub(r"^https?://[^/]+/", "", target)
        target = re.sub(r"\.git$", "", target)

        if target.casefold() != ALLOWED.casefold():
            deny(
                f"Blocked: this would target '{target}', not {ALLOWED}. "
                f"This clone is a fork of wieslawsoltes/Svg.Skia, and a pull request opened "
                f"anywhere but {ALLOWED} is public and on somebody else's repository. "
                f"Pass --repo {ALLOWED} explicitly. A shell variable is not accepted here: "
                f"the guard cannot see what it holds."
            )
        return

    # No --repo at all. Harmless for subcommands that resolve from an existing PR, but
    # `create` is the one whose default is the fork parent.
    if re.search(r"\bgh\s+pr\s+create\b", command):
        deny(
            f"Blocked: `gh pr create` without --repo. On a fork it defaults to the parent "
            f"(wieslawsoltes/Svg.Skia), so this could open a pull request on somebody else's "
            f"repository. Pass --repo {ALLOWED}."
        )


if __name__ == "__main__":
    main()
