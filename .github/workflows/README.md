# The build, and cutting a release

`build.yml` does two things, and only the first happens on its own.

## Every push

Restore, build the solution, run the protocol checks, and upload `TarnishedTool.exe` as an artifact you can download from the run. The artifact carries the minute it was built, `TarnishedTool-20260913-0316Z`, in UTC, because every artifact downloads as a zip named after itself and a folder of `TarnishedTool (4).zip` says nothing about which build is which. The checks are `tests/ProtocolChecks.csproj`, which links the two files in the control layer that know nothing about the game and asserts what goes over the wire; they are the only thing here that can be tested without a copy of Elden Ring.

The build also fails if the executable comes out under four megabytes, which is what a Costura build that stopped packing its dependencies looks like. That would otherwise ship as a download that runs on the machine that built it and nowhere else.

## Signing

Off until the secrets are set, and the build is the same unsigned executable it always was until then. Nobody's fork and no pull request fails for want of a key.

| Secret | What it is |
| --- | --- |
| `AZURE_CLIENT_SECRET` | The app registration's secret. Its presence is what turns signing on. |
| `AZURE_TENANT_ID` | The directory the app registration is in. |
| `AZURE_CLIENT_ID` | The app registration itself. |
| `AZURE_SIGNING_ENDPOINT` | Region-specific, e.g. `https://eus.codesigning.azure.net`. |
| `AZURE_SIGNING_ACCOUNT` | The signing account's name. |
| `AZURE_CERTIFICATE_PROFILE` | The certificate profile to sign under. |

The endpoint and the account have to name the region they were created in, which is the usual first thing to be wrong. The app registration needs the **Trusted Signing Certificate Profile Signer** role on the signing account, which is granted under Access control (IAM) there and is not implied by owning the subscription.

### Not SignPath

SignPath's GitHub integration is built on trusted build systems, where SignPath reads the run through the GitHub API and satisfies itself that the file came out of this build of this repository. That is the better story, and it is not on the free plan. If [SignPath Foundation](https://signpath.org) takes the project it comes back: the shape of the step is the same and only the middle of it changes.

Nothing is lost in the meantime except that verification. Azure signs the file in place, so there is no artifact handed over and taken back, and the workflow is shorter for it.

### What signing is and is not for

Not SmartScreen, in the short term. That is reputation, and a new certificate has none; it accrues over downloads.

The reason is Defender. It quarantines the tool when it attaches to the game, because writing another process's memory is exactly what its behavior rules watch for, and no signature stops a behavior rule. What a signature changes is what Microsoft can do when the false positive is reported: an unsigned file is allowed by hash, which covers the one build, and a signed one can be allowed by publisher, which covers every build after it. Report each release at <https://www.microsoft.com/wdsi/filesubmission> and the allowance stops being something to redo every time.

### It checks its own work

A signature that came back anything but `Valid` fails the build, and so does one with no timestamp. An untimestamped signature goes invalid for everybody on the day the certificate expires, and a signing step that quietly produced nothing would otherwise ship an unsigned file under a release that says it is signed, which is discovered on a tester's machine by their antivirus.

## Cutting a release

Actions → Build → Run workflow, tick **Cut a release from this build**, and give a tag. The build runs first; the release waits.

**The gate is an environment called `release`, and it does nothing until somebody configures it.** GitHub creates an environment on first use with no protection at all, so a workflow naming one is not by itself a gate. Under Settings → Environments → `release`, add **Required reviewers**. After that the release job sits in a waiting state and a named person approves it before a tag exists or a file is published.

Worth considering there too: limiting the environment's deployment branches to `integration`, so a release cannot be cut from a branch nobody has looked at.

## What a release contains

The one file, the tag as the title, whatever was typed in the notes box, and a pointer back to the commit and to `docs/controlling.md`.

Anything typed into that form reaches the script as an environment variable rather than being pasted into it. An expression is substituted before the shell sees the line, so a release note could otherwise carry anything it liked.
