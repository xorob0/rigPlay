# Contributing to rigPlay

rigPlay turns an Android tablet into a CarPlay screen for a sim-racing rig, paired with a SimHub
plugin on the PC. This file covers where things live and how work is tracked.

## Layout

| Path | What |
| --- | --- |
| `/` (`shared/`, `common/`, `mobile/`) | Android app; the Gradle build is at the repository root |
| `plugin/RigPlay` | SimHub plugin (net48 class library) |
| `plugin/RigPlay.Tests` | Plugin logic tests (.NET 8, xunit) |
| `docs/` | Install, protocol and design docs |
| `scripts/` | Repository checks and release helpers |

## Building and testing

Android (JDK 25, Android SDK):

```sh
./gradlew :shared:testDebugUnitTest :common:testDebugUnitTest :mobile:lintDebug :mobile:assembleDebug
```

Plugin (.NET 8 SDK; builds on Linux):

```sh
dotnet test plugin/RigPlay.Tests
dotnet build plugin/RigPlay -c Release
```

CI (`.github/workflows/ci.yml`) runs both on every pull request. Unit tests are not enough for
plugin changes: **verify them on the Windows VM or the rig** by loading the built `RigPlay.dll` in
SimHub, and say what you checked in the pull request.
On the development host, `scripts/vm.sh plugin` builds, installs and restarts SimHub on the VM; see
[docs/testing-vm.md](docs/testing-vm.md).

`scripts/ci-local.sh` runs the same commands as `.github/workflows/ci.yml` (public-tree check, Gradle tests/lint/debug build, `dotnet test`, Release build). Run it before pushing; `--android` or `--plugin` runs one half.


Never commit credentials, keystores, accessory identity files or APKs;
`scripts/check_public_tree.py` fails CI if you do.

## Issues, labels and milestones

Every issue gets one `area:` label, plus a `type:` label when it is not ordinary work.

| Label | Meaning |
| --- | --- |
| `area:app` | Android tablet app |
| `area:plugin` | SimHub plugin |
| `area:protocol` | PC ↔ tablet protocol and its shared fixtures |
| `area:docs` | Documentation and site |
| `area:ci` | CI, build and release tooling |
| `type:epic` | Groups child issues in a task list |
| `type:spike` | Time-boxed investigation; the outcome is a written finding, not shipped code |

Milestones: `v1` (pairing, wireless CarPlay, SimHub dashboard button, PC-presence-coupled phone
connection, CarPlay audio routed to the PC) and `v2` (later work). Put an issue in the milestone it
ships with; leave it empty while undecided.

## Commits and pull requests

- One logical change per commit. Subject in the imperative, sentence case, no trailing period,
  roughly 72 characters, ending with the issue number: `Add pairing handshake to the plugin (#21)`.
- Explain the why in the body when the diff does not make it obvious.
- Pull requests fill in the template: what changed, how it was verified, and the issue they close.
- User-visible changes get a line in `CHANGELOG.md` under the upcoming
  `# rigPlay X.Y.Z — unreleased` section.

## Releases

`VERSION` holds the version. Pushing a tag `v<VERSION>` (for example `v0.1.0`, or `v0.1.0-rc.1`
for a pre-release) runs `.github/workflows/release.yml`. It refuses the tag unless it matches
`VERSION` and `CHANGELOG.md` has a section for it, then publishes the APK, `rigPlay-plugin.zip` and
`SHA256SUMS.txt`. The APK is signed and carries the accessory identity when the repository secrets
described in [docs/BUILD.md](docs/BUILD.md#ci-builds) are set (`scripts/set-release-secrets.sh`).
A tag on a commit that is not on `main` produces a draft release, which is how to test the workflow.
