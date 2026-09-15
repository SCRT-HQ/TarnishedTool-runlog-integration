# The build, and publishing a release

`build.yml` does two things. The first happens on every push and pull request; the second on every push to `integration`, and by hand.

## Every push

Restore, build the solution, run the protocol checks, and upload `TarnishedTool.exe` as an artifact you can download from the run. The artifact carries the minute it was built, `TarnishedTool-20260913-0316Z`, in UTC, because every artifact downloads as a zip named after itself and a folder of `TarnishedTool (4).zip` says nothing about which build is which. The checks are `tests/ProtocolChecks.csproj`, which links the two files in the control layer that know nothing about the game and asserts what goes over the wire; they are the only thing here that can be tested without a copy of Elden Ring.

The build also fails if the executable comes out under four megabytes, which is what a Costura build that stopped packing its dependencies looks like. That would otherwise ship as a download that runs on the machine that built it and nowhere else.

## Every push to `integration`: a beta

Artifacts can only be downloaded by somebody signed in to GitHub, so a green build of `integration` also publishes a prerelease that anybody can download from the Releases page. It is named for the upstream release it was built on and the minute it was built:

    v1.2.1-runlog-beta-20260915-0714

The first part is the newest upstream tag reachable from where the commit and [borgCode/TarnishedTool](https://github.com/borgCode/TarnishedTool)'s `master` meet, found by fetching upstream's tags during the run. Rebase onto a newer upstream and the name follows. The rest is the build minute, UTC, the same one the artifact carries, so a release and the run that made it can be matched by eye.

A beta is marked as a prerelease, so once a real release exists GitHub never points "latest" at a beta.

## Cutting a release by hand

Actions → Build → Run workflow, tick **Cut a release from this build**, and optionally give a tag. With a tag, that is the name and it publishes as a full release. Without one, it is named and marked as a beta, exactly as a push would be. The build runs first; the release waits.

## The gate

The release job names an environment called `release`, **and it does nothing until somebody configures it.** GitHub creates an environment on first use with no protection at all, so a workflow naming one is not by itself a gate. Under Settings → Environments → `release`, add **Required reviewers**, and the release job sits in a waiting state until a named person approves it.

Bear in mind what that now means: every push to `integration` would wait on a person before its beta appears. Limiting the environment's deployment branches to `integration` is the cheaper protection, and it stops a hand-cut release from a branch nobody has looked at.

## What a release contains

The one file, the tag as the title, whatever was typed in the notes box, and a pointer back to the commit, the upstream version, and `docs/controlling.md`.

Anything typed into that form reaches the script as an environment variable rather than being pasted into it. An expression is substituted before the shell sees the line, so a release note could otherwise carry anything it liked.
