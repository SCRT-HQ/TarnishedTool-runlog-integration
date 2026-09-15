# The build, and publishing a release

`build.yml` does two things. The first happens on every push and pull request; the second is offered after every push to `integration`, and by hand, and happens when somebody approves it.

## Every push

Restore, build the solution, run the protocol checks, and upload `TarnishedTool.exe` as an artifact you can download from the run. The artifact carries the minute it was built, `TarnishedTool-20260913-0316Z`, in UTC, because every artifact downloads as a zip named after itself and a folder of `TarnishedTool (4).zip` says nothing about which build is which. The checks are `tests/ProtocolChecks.csproj`, which links the two files in the control layer that know nothing about the game and asserts what goes over the wire; they are the only thing here that can be tested without a copy of Elden Ring.

The build also fails if the executable comes out under four megabytes, which is what a Costura build that stopped packing its dependencies looks like. That would otherwise ship as a download that runs on the machine that built it and nowhere else.

## Every push to `integration`: a beta, on approval

Artifacts can only be downloaded by somebody signed in to GitHub, so a green build of `integration` offers a prerelease that anybody can download from the Releases page. The Release job waits for a reviewer; it shows as waiting among the checks on the commit, and on the pull request that carries it. Open the run, **Review deployments**, approve, and the release is published. Reject it, or leave it, and nothing is. A newer push to `integration` cancels a release still waiting, so only the latest build is ever on offer.

A beta is named for the upstream release it was built on and the minute it was built:

    v1.2.1-runlog-beta-20260915-0714

The first part is the newest upstream tag reachable from where the commit and [borgCode/TarnishedTool](https://github.com/borgCode/TarnishedTool)'s `master` meet, found by fetching upstream's tags during the run. Rebase onto a newer upstream and the name follows. The rest is the build minute, UTC, the same one the artifact carries, so a release and the run that made it can be matched by eye.

A beta is marked as a prerelease, so once a real release exists GitHub never points "latest" at a beta.

## Cutting a release by hand

Actions → Build → Run workflow, tick **Cut a release from this build**, and optionally give a tag. With a tag, that is the name and it publishes as a full release. Without one, it is named and marked as a beta, exactly as a push would be. The build runs first; the release waits.

## The gate

The release job names an environment called `release`, and the gate is whatever that environment is configured to require, under Settings → Environments → `release`. As set up, it has a **required reviewer** and its **deployment branches** are limited to `integration`, so a release cannot be approved from a branch nobody has looked at. A workflow naming an environment is not by itself a gate: GitHub creates one on first use with no protection at all, and if the environment is ever deleted and recreated, every push would publish unasked until it is configured again.

## What a release contains

The one file, the tag as the title, whatever was typed in the notes box, and a pointer back to the commit, the upstream version, and `docs/controlling.md`.

Anything typed into that form reaches the script as an environment variable rather than being pasted into it. An expression is substituted before the shell sees the line, so a release note could otherwise carry anything it liked.
