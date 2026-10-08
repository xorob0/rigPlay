# Audit quick wins QW-1…QW-5 on `chore/audit-quick-wins`

Five scoped fixes from the audit: lenient `status.nav` decoding on the Kotlin side to match the C# reference (QW-1), a 1–128 bound on `hostId` for beacon/welcome in C# (QW-2), the 8192-byte PCM payload cap on the Kotlin audio decoder plus a shared cross-parsed invalid fixture (QW-3), idempotent executor shutdown in `CarPlayHostActivity.onDestroy()` (QW-4), and test-tone cleanup on `onPause()` in `RigPlayActivity` (QW-5). The C# half is build-verified (689 tests pass, +8 over the 681 baseline; Release build clean); the Kotlin half cannot be compiled in this environment and is declared CI-validated, which is the correct posture. Each quick win is one commit, eleven files total, and `docs/protocol.md` is untouched.

Watch for: QW-3 required an undocumented second C# codec touch (`Audio/AudioHeader.cs`) that the plan did not anticipate — it is squarely within QW-3's intent and transparently documented, but it is a scope expansion worth noting (confirmed).

**Verdict**: APPROVED

## High-level view

The QW-1 Kotlin `decodeNav` is a faithful translation of the C# `DecodeNav`: wrong-type or out-of-range members become absent, a missing/invalid `maneuver` drops the whole `nav`, and a non-object `nav` yields no guidance — none of which can turn the status Malformed. The non-nullable `NavStatus.maneuver` is why an invalid maneuver drops the entire `nav` in Kotlin where C# keeps a `NavInfo` with `Maneuver=null`; the two representations are behaviorally equivalent. The frozen assertion that previously expected `assertMalformed` on `"nav":{"distanceM":5}` is corrected to expect a valid status with `nav` absent, and three further lenient cases are added.

QW-2 switches the two unbounded `ReqString(o, "hostId")` calls to the `1,128` overload already used for other identifiers in the same file, with two `[Fact]`s for the 129-char case and InlineData rows for empty and well-formed hostIds on both beacon and welcome.

QW-3 adds a named `MAX_PAYLOAD_BYTES = 8192` constant and a `payload > cap` guard placed before the format branch in the Kotlin decoder, mirroring the C# position. The shared fixture gains a `pcm-payload-over-8192` vector (8194-byte payload, whole mono frames, so size is the sole violation) exercised by both suites. Enforcing the cap on the C# side required also fixing `Audio/AudioHeader.cs`, whose `TryParse` only checked the lower bound — the plan had assumed C# already rejected oversize payloads everywhere.

QW-4 and QW-5 are small lifecycle fixes: `onDestroy()` now shuts down both worker executors idempotently so a system-driven recreate that skipped `shutdown()` does not leak threads, and the test-tone cleanup is extracted into `stopTestTone()` and called from both `playTestTone()` and `onPause()`.

<details>
<summary>Issues (1)</summary>

1. **QW-3 scope expansion** — the cap could not be enforced in C# without also editing `plugin/RigPlay/Audio/AudioHeader.cs` (sister codec checked only the lower bound). Within QW-3 intent and documented in `verification.md`; informational, not blocking.

</details>

<details>
<summary>Details</summary>

### QW-1 — lenient `status.nav` matches the C# reference

The strict block that threw `BadMember` on any wrong-typed `nav` member — propagating out of `decodeStatus` and making the whole status Malformed — is replaced by `decodeNav(f.optJsonObject("nav"))`. `optJsonObject` reads the raw member via `raw(name) as? JSONObject`, so a `nav` that is absent, JSON null, or not an object all yield `null` without throwing. That is the "no route guidance, status still valid" path, and the `"nav":5` test case exercises it.

Member handling is a line-by-line match with the C# `DecodeNav`:

```
maneuver  : (opt as? String) length 1..64, else drop whole nav   ~ LenientString + length guard, Maneuver=null
distanceM : (opt as? Number).toDouble(), finite & 0..Int.Max,     ~ LenientDecimal(0,int.Max,true) then
            Math.round away from zero (negatives already excluded)   Math.Round(AwayFromZero)
road      : (opt as? String) non-blank                            ~ LenientString + IsNullOrWhiteSpace
etaEpochS : Int/Long only, >= 0                                   ~ JTokenType.Integer, >= 0
```

The one representational difference is deliberate and correct: `NavStatus.maneuver` is non-nullable, so an invalid maneuver returns `null` (no `NavStatus`), whereas C# returns a `NavInfo` with `Maneuver=null`. The plan calls this out, and because a maneuver-less `NavStatus` cannot exist in Kotlin the two are behaviorally equivalent. `distanceM` rounds half-up via `Math.round`, which equals away-from-zero for the non-negative values that survive the range check, so the C# rounding mode is matched for every value that reaches it.

The leniency is local to the nav decode. `optJsonObject` is a new accessor on `Fields`; the strict shared accessors used by every other decoder are untouched, so no other message type loosens.

The four assertions in `statusNavIsOptionalAndArtworkStaysWithinOneLine` each match the new logic: `{distanceM:5}` and `{distanceM:350,road:"B258"}` have no maneuver → `status` with nav absent; `{maneuver:"leftTurn",distanceM:"x"}` keeps the maneuver and drops the bad distance → `status.copy(nav = NavStatus(maneuver="leftTurn"))` (the other NavStatus fields default to null); `{"nav":5}` is not an object → nav absent. The round-trip and artwork assertions are left unchanged.

### QW-2 — `hostId` bounded 1–128

Both `DecodeBeacon` and `DecodeWelcome` now call `ReqString(o, "hostId", 1, 128)`, the same bounded overload already applied to `tabletId` in `DecodeHello`. An empty or >128-char hostId raises `ProtocolException`, surfaced as `DecodeFailure.Invalid`. Coverage is symmetric across both message types: a `[Fact]` builds a 129-char hostId with `new string('a', 129)` for beacon and for welcome, and the `ContentRules` theory gains an empty-hostId row and a well-formed-hostId row for each, so the bound is shown to reject the out-of-range cases without over-rejecting a normal id. The existing 36-char UUID samples in the discovery/control tests stay within range.

### QW-3 — 8192-byte cap on both sides, plus a sister-codec fix

The Kotlin decoder gains `MAX_PAYLOAD_BYTES = 8192` and `if (payloadLength > AudioHeader.MAX_PAYLOAD_BYTES) return null` placed before the format-specific branch, so the cap covers PCM (previously only lower-bounded) and leaves the already-tighter Opus bound (1275) effective. The guard position mirrors C#, where the cap is checked before the format branch.

The shared `pcm-payload-over-8192` vector is a valid 12-byte PCM mono header (`000001000000000001e00101`) followed by an 8194-byte all-zero payload — one whole-frame step over the cap, so the oversize is the sole reason for rejection and nothing else in the header can mask it. Both suites iterate the `invalid` array generically, so the vector is exercised by `AudioHeaderTests.RejectsInvalidVector`, `ProtocolFixturesTests.AnInvalidAudioVectorIsRejected` (C#), and the Kotlin `ProtocolFixturesTest` audio block, with the frozen `invalid` count bumped 15→16 in `AudioHeaderTests`.

Enforcing the cap on the C# side exposed that only `Protocol/AudioHeader.cs` (`AudioDatagram.Validate`) applied §10.2; the sister `Audio/AudioHeader.cs` (`AudioHeader.TryParse`, used by `RejectsInvalidVector`) checked only the lower bound and would have accepted the new vector. The fix adds `AudioHeaderError.PayloadTooLarge` and the matching `payload > MaxPayloadBytes` guard, bringing the two C# codecs into agreement with each other and the spec. This is a file the plan did not list, but it is the minimum needed to make the shared fixture pass on the C# side and stays within QW-3's stated intent of enforcing the §10.2 cap on both sides; `verification.md` documents it explicitly.

### QW-4 — idempotent executor shutdown on destroy

`onDestroy()` now calls `teardownExecutor.shutdown()` and `airPlayCommandExecutor.shutdown()` after the existing session-log cleanup and before `super.onDestroy()`. The previous path shut these down only inside `shutdown()` (and the teardown executor from within its own `execute` block), so a system-driven recreate that never ran `shutdown()` leaked both threads. `shutdown()` tolerates a repeat call, so the pair is safe when `shutdown()` already ran, and there is no blocking `awaitTermination` on the main thread.

### QW-5 — test-tone cleanup bounded to the visible session

The head-of-`playTestTone` cleanup is extracted into `stopTestTone()`, which removes the pending `toneStop` callback, stops and releases `testToneTrack`, and nulls both behind `?.let` guards. `playTestTone()` now opens with `stopTestTone()` (replacing the inline cleanup, so no double release) and `onPause()` calls it before `super.onPause()`. This bounds the leaked AudioTrack and the up-to-4.5s pending main-thread callback to the foreground session. The brief mentioned `onStop` as an alternative hook; the plan selected `onPause`, which is the earlier and sufficient lifecycle point, so this is a deliberate choice rather than a gap.

### Verification evidence

`verification.md` records `dotnet test plugin/RigPlay.Tests` at 689/689 (baseline 681, +8) and `dotnet build plugin/RigPlay -c Release` clean with 0 warnings. The +8 reconciles with the diff: two beacon/welcome 129-char `[Fact]`s, four `ContentRules` rows, and one fixture vector run by two C# theories. The Kotlin changes (QW-1 code + test, QW-3 Kotlin guard, QW-4, QW-5) are declared "non compilé localement, à valider par la CI" against the documented gradle command and are not claimed as build-verified — the correct posture given no JDK/Android SDK here. Per the review instructions the suite was not re-run; the one spot-check performed was measuring the fixture payload (8194 bytes, confirming size is the sole violation).

### Scope

Eleven files, all within QW-1…QW-5: `SimHubProtocol.kt`, `ProtocolFixturesTest.kt`, `Messages.cs`, `MessageCodecTests.cs`, `SimHubAudioHeader.kt`, `audio-header.json`, `AudioHeaderTests.cs`, `Audio/AudioHeader.cs`, `CarPlayHostActivity.kt`, `RigPlayActivity.kt`, and the `verification.md` note. No QW-6…QW-9 work and `docs/protocol.md` is unmodified. The only file beyond the plan's named set is `Audio/AudioHeader.cs`, justified above.

</details>

<details>
<summary>File map</summary>

- `shared/.../simhub/SimHubProtocol.kt` — strict nav block replaced by lenient `decodeNav` + `optJsonObject` accessor (QW-1).
- `shared/.../simhub/ProtocolFixturesTest.kt` — frozen `assertMalformed` corrected to valid-status-nav-absent, +3 lenient cases (QW-1).
- `plugin/RigPlay/Protocol/Messages.cs` — `hostId` bounded 1–128 in `DecodeBeacon`/`DecodeWelcome` (QW-2).
- `plugin/RigPlay.Tests/MessageCodecTests.cs` — 2 Facts (129-char) + 4 InlineData rows for beacon/welcome hostId (QW-2).
- `shared/.../simhub/SimHubAudioHeader.kt` — `MAX_PAYLOAD_BYTES = 8192` + pre-branch cap guard (QW-3).
- `protocol/fixtures/audio-header.json` — `pcm-payload-over-8192` invalid vector, 8194-byte payload (QW-3).
- `plugin/RigPlay.Tests/Audio/AudioHeaderTests.cs` — frozen invalid count 15→16 (QW-3).
- `plugin/RigPlay/Audio/AudioHeader.cs` — `PayloadTooLarge` error + cap guard in `TryParse`, sister-codec parity (QW-3).
- `common/.../CarPlayHostActivity.kt` — idempotent executor shutdown in `onDestroy()` (QW-4).
- `common/.../RigPlayActivity.kt` — `stopTestTone()` helper, called from `playTestTone` and `onPause` (QW-5).
- `.agents/tasks/quick-wins/verification.md` — verification note.

Full diff: `git -C d:\Project\rigPlay\.worktrees\quick-wins-audit diff main...chore/audit-quick-wins`

</details>
