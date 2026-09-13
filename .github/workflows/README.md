# The build, and cutting a release

`build.yml` does two things, and only the first happens on its own.

## Every push

Restore, build the solution, run the protocol checks, and upload `TarnishedTool.exe` as an artifact you can download from the run. The checks are `tests/ProtocolChecks.csproj`, which links the two files in the control layer that know nothing about the game and asserts what goes over the wire; they are the only thing here that can be tested without a copy of Elden Ring.

The build also fails if the executable comes out under four megabytes, which is what a Costura build that stopped packing its dependencies looks like. That would otherwise ship as a download that runs on the machine that built it and nowhere else.

## Cutting a release

Actions → Build → Run workflow, tick **Cut a release from this build**, and give a tag. The build runs first; the release waits.

**The gate is an environment called `release`, and it does nothing until somebody configures it.** GitHub creates an environment on first use with no protection at all, so a workflow naming one is not by itself a gate. Under Settings → Environments → `release`, add **Required reviewers**. After that the release job sits in a waiting state and a named person approves it before a tag exists or a file is published.

Worth considering there too: limiting the environment's deployment branches to `integration`, so a release cannot be cut from a branch nobody has looked at.

## What a release contains

The one file, the tag as the title, whatever was typed in the notes box, and a pointer back to the commit and to `docs/controlling.md`.

Anything typed into that form reaches the script as an environment variable rather than being pasted into it. An expression is substituted before the shell sees the line, so a release note could otherwise carry anything it liked.
