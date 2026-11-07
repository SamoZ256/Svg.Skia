---
description: Publish a new SamoZ256.* release from master's tip all the way to nuget.org — tag it, watch release.yml, approve the nuget deployment, and check the packages arrived
allowed-tools: Read, Skill, Bash(git fetch:*), Bash(git tag:*), Bash(git push origin v*), Bash(git log:*), Bash(git rev-parse:*), Bash(gh run:*), Bash(gh api:*), Bash(gh release view:*), Bash(curl -s https://api.nuget.org/*)
---

Publish the next release of the packages, from `origin/master`'s tip to nuget.org.

**Invoking this is the permission for both steps that cannot be taken back:** pushing the tag, which
is the publication, and approving the `nuget` deployment that `release.yml` waits on. The
`publish-packages` skill asks before each unless told in so many words; this command is those
words. The permission is this invocation and nothing after it: a second release asks again.

Follow `.claude/skills/publish-packages/SKILL.md` step by step — it has the commands, the version
rule, and what each failure means. Read it first and do not repeat it from memory. Where this
command says more than the skill, this command wins:

1. **Master** must have finished every run on its tip, none failed. Wait for one still running;
   stop on a red one rather than tag over it. Say what the release carries.
2. **Version.** Say the version before tagging it. Stop if that tag already exists, or if
   nuget.org already lists the version, rather than picking around it.
3. **Tag** `origin/master` itself, annotated, and push only that tag.
4. **Approve** the run's pending `nuget` deployment through the API, with the comment
   `Approved by /release`, once the run is `waiting` there and **Build, Test, Pack and Pack MAUI
   have all succeeded**. The publish job needs only the build and the two packs, so it can be
   waiting while Test is still running: wait for Test too. If anything failed, approve nothing and
   follow the skill's failure cases.
5. **Check** that the release holds every package, that the publish job pushed each one, and that
   nuget.org lists the version, allowing for indexing.

Stop at the first thing that looks wrong. Never force a push, never push `--tags`, never move or
re-push a tag onto another commit, and never approve a run other than the one this tag started.

Report the version, the commit it was tagged on, the run and the release with their links, how many
packages went out, and what nuget.org lists so far. Say that consumers — TapHome's `SamoZ256.*`
references — move to the new version by hand.
