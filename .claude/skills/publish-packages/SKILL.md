---
name: publish-packages
description: Publish a new SamoZ256.* NuGet release of Svg.Skia from master — pick the next -samoz.N version, tag master, watch release.yml push every package to nuget.org, and check they arrived. Use when asked to publish, release or ship the packages, or to cut a new version.
---

# Publish the packages from master

A release is a tag. Pushing `v<VersionPrefix>-samoz.<N>` runs `.github/workflows/release.yml`,
which builds, tests on Linux, packs every package — 32 from the solution and 2 MAUI ones packed on
macOS — pushes them all to nuget.org as `SamoZ256.<project>`, and makes a GitHub pre-release with
the packages attached. `v5.2.2-samoz.2` and `v5.2.2-samoz.3` each took 15 minutes from tag to
release and pushed 34 packages.

The suffix is the release; the prefix depends on the package. 26 packages take `VersionPrefix`
(`5.2.2-samoz.3`), and the UI packages take their framework's prefix from `Directory.Build.props`:
the 4 Avalonia ones `12.0.0.16-samoz.3`, the 2 Uno ones `6.5.31.17-samoz.3`, the 2 MAUI ones
`10.0.0.12-samoz.3`.

Nothing on nuget.org can be deleted, only unlisted, so **pushing the tag is the publication**. Ask
before it unless the person asked for a release in so many words.

Not `workflow_dispatch`: the release step names the GitHub release after `github.ref_name`, which
for a run dispatched on `master` is `master`.

## 1. Check master is worth publishing

```sh
git fetch origin --tags
gh run list --repo SamoZ256/Svg.Skia --branch master --commit "$(git rev-parse origin/master)" \
  --json workflowName,status,conclusion
```

Every run there must have finished and none failed; wait for one in progress
(`gh run watch <id> --repo SamoZ256/Svg.Skia --exit-status`, in the background). `release.yml`
tests on Linux only, so `master`'s Build run is the only one that tested Windows and macOS. Never
tag a commit that is not `origin/master`'s tip, and never a red one.

Say what the release carries: `git log --oneline --first-parent <last tag>..origin/master`.

## 2. Pick the version

`<VersionPrefix>` is in `Directory.Build.props`; it tracks upstream's and is not bumped here. `<N>`
is one more than the highest of the existing tags and what nuget.org already has:

```sh
git tag -l 'v*-samoz.*' --sort=-v:refname | head -3
curl -s https://api.nuget.org/v3-flatcontainer/samoz256.svg.skia/index.json
```

Where `VersionPrefix` has moved past every tag, `<N>` starts again at 1. The workflow refuses a tag
without a suffix.

## 3. Tag and push the tag

```sh
git tag -a v<prefix>-samoz.<N> origin/master -m "Svg.Skia <prefix>-samoz.<N>"
git push origin v<prefix>-samoz.<N>
```

Annotated, on `origin/master` itself rather than on whatever is checked out, and only the tag is
pushed — `git push origin v5.2.2-samoz.3`, never `--tags`, which would push every tag this clone
holds.

## 4. Watch the run

```sh
gh run list --repo SamoZ256/Svg.Skia --workflow release.yml --limit 1 \
  --json databaseId,headBranch,status
gh run watch <id> --repo SamoZ256/Svg.Skia --exit-status
```

Watch in the background and act on the notification: it runs about a quarter of an hour. On a
failure, `gh run view <id> --repo SamoZ256/Svg.Skia --log-failed`.

**Publish to nuget.org waits for approval.** Its job runs in the `nuget` environment, which only a
`v*` tag may deploy to and which waits for its required reviewer, the repository's owner. The run
sits at `waiting` once the packages are packed. Approving is the reviewer's decision, not this
skill's: tell the person the run is ready, with its link, and let them press **Review deployments**
on it. Approve through the API only when they have said in so many words to approve it for them:

```sh
gh api repos/SamoZ256/Svg.Skia/actions/runs/<id>/pending_deployments --jq '.[].environment.id'
gh api -X POST repos/SamoZ256/Svg.Skia/actions/runs/<id>/pending_deployments \
  -F 'environment_ids[]=<environment id>' -f state=approved -f comment='<who asked>'
```

- **Build, Test or Pack failed:** nothing reached nuget.org. Fix it through a pull request, delete
  the tag (`git push origin :refs/tags/<tag>` and `git tag -d <tag>`), and tag the fixed `master`
  with the same version.
- **Publish to nuget.org failed part way:** some packages are already public under that version.
  Re-run the failed jobs (`gh run rerun <id> --repo SamoZ256/Svg.Skia --failed`); `--skip-duplicate`
  passes over what is there. Never move the tag to another commit: the version would then name two
  different builds.
- **The NuGet login step failed:** Trusted Publishing needs the nuget.org policy for
  `SamoZ256/Svg.Skia`, `release.yml` and the `nuget` environment, and the `NUGET_USER` secret.
  Neither can be fixed from here; say which and stop.

Every action in `release.yml` is pinned to a commit, with its version in a comment, because the
publish job holds an hour's nuget.org key. Moving one to a newer version means pinning that
version's commit, never putting a tag back: `git ls-remote https://github.com/<owner>/<action>
'refs/tags/<tag>*'` lists it, and an annotated tag's commit is on its `^{}` line.

## 5. Check it arrived

```sh
gh release view <tag> --repo SamoZ256/Svg.Skia --json name,isPrerelease,assets --jq '.assets | length'
gh run view <id> --repo SamoZ256/Svg.Skia --log | grep -c "Your package was pushed."
curl -s https://api.nuget.org/v3-flatcontainer/samoz256.svg.studio.projects/index.json
```

The release holds the 34 packages and the publish job says each was pushed. nuget.org validates
and indexes a push before it lists the version, which takes minutes; until then the index is one
version short, and that alone is not a failure. Look a UI package up under its own prefix, not
`VersionPrefix`.

Report the version, the commit it was tagged on, the run and the release, and how many packages
went out. A project consuming them — TapHome's `SamoZ256.*` references — moves to the new version
by hand.
