# Implementation Plan — Audit Quick Wins (QW-1 … QW-5)

All work happens inside the worktree `d:\Project\rigPlay\.worktrees\quick-wins-audit`
on branch `chore/audit-quick-wins`. Never read or edit files under `d:\Project\rigPlay`
directly. Paths below are relative to the worktree root unless absolute.

## Scope guards (MUST hold for every item)

- Implement ONLY QW-1 … QW-5 below. Do NOT implement QW-6 … QW-9 or any other audit item.
- Do NOT touch any file outside those named in the items below.
- Do NOT modify `docs/protocol.md`. The target behavior is already described there
  (§6.7.1 for nav leniency, §1/§6.1/§6.2 for `hostId` 1–128, §10.2 for the 8192-byte payload cap).
- Keep each commit to one logical change (CONTRIBUTING.md). One commit per QW is a good default.

## Build & test commands (discovered during exploration)

- Plugin (.NET): from the worktree root run
  - `dotnet test plugin/RigPlay.Tests`
  - `dotnet build plugin/RigPlay -c Release`
  - SDK present: .NET 10 SDK (`dotnet --version` → 10.0.204); the projects target `net8.0`
    and build/test fine on this SDK. Baseline before changes: 681 tests. After the changes
    the count MUST be at least 681 plus the new ones, and all MUST pass.
- Android/Kotlin: the project needs JDK 25 + Android SDK (CONTRIBUTING.md), which are NOT
  available in this environment. Kotlin changes CANNOT be compiled or tested locally.
  Kotlin correctness is ensured by careful reading and consistency with the existing tests.
  Mark all Kotlin work "non compilé localement, à valider par la CI (`.github/workflows/ci.yml`)"
  and do NOT mark it build-verified. The CI command the Kotlin tests run under is
  `./gradlew :shared:testDebugUnitTest :common:testDebugUnitTest :mobile:lintDebug :mobile:assembleDebug`.

Note on cross-parsed fixtures: `protocol/fixtures/audio-header.json` is parsed by BOTH the C#
suite (`plugin/RigPlay.Tests/ProtocolFixturesTests.cs`) and the Kotlin suite
(`shared/src/test/java/com/shilapi/xcertplay/simhub/ProtocolFixturesTest.kt`). A change to that
file affects both; QW-3 relies on this.

---

- [ ] 1. (QW-1, Kotlin) Decode `status.nav` leniently in `SimHubProtocol.kt`, matching protocol.md §6.7.1 and the C# `DecodeNav`.
      Current behavior: `decodeStatus` (around lines 393–401) decodes `nav` strictly:
      `f.optObject("nav")?.let { NavStatus(maneuver = it.reqString("maneuver", 1..64), distanceM = it.optInt(...), road = it.optString("road"), etaEpochS = it.optInt(...)) }`.
      Any wrong-typed/out-of-range member (or a `nav` that is not an object) throws `BadMember`,
      which propagates out of `decodeStatus` and makes the WHOLE status Malformed.
      Target behavior (exactly the C# `DecodeNav`, Messages.cs ~lines 878–917): a `nav` that is not
      an object → nav ABSENT (status still valid); each member with the wrong JSON type or out of
      range → that member treated as absent; a missing or invalid `maneuver` (empty, >64 chars, or
      wrong type) → nav ABSENT; a blank `road` → absent (matches C# `IsNullOrWhiteSpace`); a bad
      `nav` NEVER makes the status invalid — the rest of the status is used.
      How to implement: replace the strict `nav` block with a lenient decode that does NOT let
      `BadMember` escape. Model it on the existing lenient `decodeTelemetry` (same file, which reads
      members directly off the `JSONObject` with `as?` casts and range checks, returning null on
      mismatch rather than throwing). Because `nav` is a nested object, read the raw nested
      `JSONObject` for `nav` (if `optObject("nav")` throws because `nav` is present but not an object,
      treat that as absent — e.g. guard with the raw value being a `JSONObject`), then per member:
      - `maneuver`: must be a non-empty String of length 1..64; otherwise the whole `nav` is absent
        (return `null`). (C#: a maneuver that is null/empty/>MaxLength makes `Maneuver` null, and the
        Kotlin `NavStatus.maneuver` is non-nullable, so no valid maneuver ⇒ no `NavStatus`.)
      - `distanceM`: integer ≥ 0; a decimal is rounded to whole metres away from zero (C# behavior);
        wrong type/out of range → absent (null).
      - `road`: String; blank/whitespace-only → absent (null).
      - `etaEpochS`: integer ≥ 0; wrong type/out of range → absent (null).
      Keep the lenient helper local to the status decode (do not loosen the shared strict `Fields`
      accessors used elsewhere). Preserve the existing `NavStatus` data class shape
      (`SimHubMessage.kt`: `maneuver: String`, `distanceM: Int?`, `road: String?`, `etaEpochS: Long?`)
      and the existing encode path (`navJson`, ~lines 266–269) — do NOT change encoding.
      Files: `shared/src/main/java/com/shilapi/xcertplay/simhub/SimHubProtocol.kt`
      Verify: Kotlin not compilable locally — review for consistency with `decodeTelemetry` and the
      C# `DecodeNav`. Validated by CI (`:shared:testDebugUnitTest`). Confirm the test changes in
      item 2 would pass under the new logic.

- [ ] 2. (QW-1, Kotlin test) Fix the test that freezes the wrong `nav` behavior and add lenient cases.
      Current behavior: in `ProtocolFixturesTest.kt`, test `statusNavIsOptionalAndArtworkStaysWithinOneLine`
      (around lines 215–227) asserts
      `assertMalformed("""{"type":"status",...,"nav":{"distanceM":5}}""", "status")` — i.e. a `nav`
      with no `maneuver` makes the status Malformed. After the QW-1 fix that status must be VALID with
      `nav` ABSENT.
      Target behavior: replace that `assertMalformed(...)` line with an assertion that the same line
      parses to a VALID `Status` whose `nav` is `null` (phoneConnected=true, screen=CARPLAY,
      nowPlaying=null). Use `SimHubProtocol.parse(...)` and assert the result is
      `SimHubParseResult.Ok(SimHubMessage.Status(phoneConnected = true, screen = Screen.CARPLAY, nowPlaying = null))`
      (nav defaults to null). Then ADD, within the same test and matching the existing structure, at
      least two more lenient cases, each asserting a VALID status with `nav` absent:
      (a) a `nav` object WITHOUT `maneuver` but WITH other members
          (e.g. `"nav":{"distanceM":350,"road":"B258"}`) → valid, nav null;
      (b) a `nav` with a WRONG-TYPE member that in the strict path would have thrown
          (e.g. `"nav":{"maneuver":"leftTurn","distanceM":"x"}`) → valid, with `nav` present and
          `maneuver="leftTurn"`, `distanceM=null` (a bad member is dropped, the rest of nav is used);
          OR, to keep it simplest, `"nav":{"maneuver":123}` (wrong-type maneuver) → valid, nav null.
      Keep the existing valid round-trip assertion (`nav = NavStatus(maneuver = "leftTurn")`) and the
      artwork one-line assertions unchanged.
      Files: `shared/src/test/java/com/shilapi/xcertplay/simhub/ProtocolFixturesTest.kt`
      Verify: Kotlin not compilable locally — ensure the asserted outcomes exactly match the QW-1
      logic from item 1. Validated by CI (`:shared:testDebugUnitTest`).

- [ ] 3. (QW-2, C#) Bound `hostId` to 1–128 characters when decoding `beacon` and `welcome`.
      Current behavior: `Messages.cs` `DecodeBeacon` (line ~752) and `DecodeWelcome` (line ~783) call
      `ReqString(o, "hostId")` with NO length bounds. The Kotlin side already bounds `hostId` to
      1..128 (`SimHubProtocol.kt` `decodeBeacon`/`decodeWelcome` use `reqString("hostId", 1..128)`),
      and protocol.md §1/§6.1/§6.2 states `hostId` is 1–128 characters.
      Target behavior: use the bounded overload `ReqString(o, "hostId", 1, 128)` in BOTH methods, as
      already done for other fields in this file (e.g. `ReqString(o, "tabletId", 1, 64)` in
      `DecodeHello`). An empty or >128-char `hostId` ⇒ a `ProtocolException` (rejected), matching the
      existing `AsString`/`ReqString` min/max behavior.
      Files: `plugin/RigPlay/Protocol/Messages.cs`
      Verify: `dotnet test plugin/RigPlay.Tests` passes (with item 4's new cases); existing
      beacon/welcome tests (DiscoveryBeaconTests, ControlServerTests) still pass since their sample
      hostId (36-char UUID) stays within 1..128.

- [ ] 4. (QW-2, C# test) Add rejection cases for out-of-bounds `hostId` on beacon and welcome.
      Add `[InlineData]` rows to the existing `ContentRules` theory in `MessageCodecTests.cs` (it
      asserts `MessageCodec.TryDecode(line).Ok == valid`). Cover, with `valid: false`:
      - beacon with empty hostId:
        `{"type":"beacon","name":"RIG-PC","hostId":"","version":"0.1.0","controlPort":23711,"audioPort":23712,"protocol":1}`
      - beacon with a 129-char hostId (use `new string('a', 129)` built into the literal, as
        `ATabletIdOf65CharactersIsInvalid` does, or a plain over-long literal in an InlineData row)
      - welcome with empty hostId:
        `{"type":"welcome","hostId":"","name":"RIG-PC","version":"0.1.0","protocol":1,"features":[]}`
      - welcome with a 129-char hostId.
      Also add one `valid: true` sanity row for a well-formed beacon and welcome with a normal hostId
      to show the bound does not over-reject. If an over-long literal is awkward inside `[InlineData]`,
      instead add two `[Fact]` tests (mirroring `ATabletIdOf65CharactersIsInvalid`) that build the
      129-char hostId with `new string('a', 129)` and assert
      `MessageCodec.TryDecode(line).Failure == DecodeFailure.Invalid` for beacon and welcome.
      Files: `plugin/RigPlay.Tests/MessageCodecTests.cs`
      Verify: `dotnet test plugin/RigPlay.Tests` — the new rows/facts pass and the total test count
      rises above 681.

- [ ] 5. (QW-3, Kotlin) Apply the 8192-byte high payload cap for PCM when decoding the audio datagram header.
      Current behavior: `SimHubAudioHeader.kt` `SimHubAudioCodec.decode(...)` validates the PCM branch
      only on the LOWER bound: `else if (payloadLength < 2 * channels || payloadLength % (2 * channels) != 0) return null`.
      There is no high bound, so an over-large PCM payload is accepted. C# already enforces the cap in
      `AudioDatagram.Validate` / `AudioHeader.Validate` via `AudioHeader.MaxPayloadBytes = 8192`
      (`plugin/RigPlay/Protocol/AudioHeader.cs` and `plugin/RigPlay/Audio/AudioHeader.cs`), returning a
      rejection when `payload > MaxPayloadBytes`.
      Target behavior: reject (return `null`) any datagram whose payload exceeds 8192 bytes. Match the
      C# rule, which applies the cap to BOTH PCM and Opus payloads (`payload > MaxPayloadBytes` is
      checked before the format-specific checks). Opus is already bounded to
      `1..MAX_OPUS_PACKET_BYTES` (1275) and so is already under 8192; add an explicit
      `payloadLength > 8192 → return null` guard for the PCM path (placing it so it also covers the
      shared path is acceptable and matches C#). Introduce a named constant for 8192 in the
      `AudioHeader` companion (e.g. `const val MAX_PAYLOAD_BYTES = 8192`, documented like the C#
      `MaxPayloadBytes` with the §10.2 reference) and use it in the guard; do not hardcode a bare 8192
      literal in the branch.
      Files: `shared/src/main/java/com/shilapi/xcertplay/simhub/SimHubAudioHeader.kt`
      Verify: Kotlin not compilable locally — review for parity with C# `AudioHeader.Validate`.
      Validated by CI (`:shared:testDebugUnitTest`). The shared invalid fixture added in item 6 is the
      cross-cutting check.

- [ ] 6. (QW-3, shared fixture) Add a `pcm-payload-over-8192` invalid vector to the shared audio fixture.
      Current behavior: `protocol/fixtures/audio-header.json` has an `invalid` array (15 vectors). BOTH
      suites iterate it generically: the C# `AnInvalidAudioVectorIsRejected` theory (asserts
      `AudioDatagram.Validate(...) != null` AND `AudioDatagram.Decode(...)` throws `ProtocolException`),
      and the Kotlin `ProtocolFixturesTest` audio block (asserts
      `SimHubAudioCodec.decode(datagramHex.unhex(), direction) == null` for every invalid vector). So a
      new invalid vector is automatically exercised by both sides — no new test method is needed, but
      BOTH decoders must reject it (C# already does; Kotlin does only after item 5).
      Target behavior: append one object to the `invalid` array:
      `{"name":"pcm-payload-over-8192","datagramHex":"<header + 8193-byte PCM payload>",
        "reason":"PCM payload of 8193 bytes exceeds the 8192-byte cap (section 10.2)."}`.
      The datagram is a valid 12-byte PCM header followed by an 8193-byte payload (one byte over the
      cap). Choose header fields that are otherwise valid so the ONLY reason for rejection is the size:
      e.g. seq 0, streamType 1 (media), flags 0, timestamp 0, sampleRateField 480 (48 kHz), channels 1,
      format 1 → header hex `000001000000000001e00101`. For PCM mono, frame size is 2 bytes; 8193 is odd
      so it is also not a whole number of frames — to make the OVERSIZE the operative failure and keep
      the vector unambiguous, prefer channels 1 with an 8194-byte payload if a whole-frame payload over
      the cap is wanted, OR keep 8193 and update the reason to note the size cap (both C# and the
      post-item-5 Kotlin reject on size first/also). Recommended: channels 1, payload 8194 bytes (a
      whole number of 2-byte frames) so size is the sole violation; header hex then stays
      `000001000000000001e00101` and `datagramHex` = that header + 8194 bytes (16388 payload hex chars)
      of arbitrary PCM (e.g. all `00`). Note the direction defaults to tabletToPc (streamType 1), which
      both suites handle without a `direction` member.
      Generating the hex: the implementer may compute it with a short throwaway script
      (e.g. `python -c "print('000001000000000001e00101'+'00'*8194)"`) — do NOT leave a placeholder.
      After adding it, the Kotlin assertion `invalid.length() >= 12` and the invalid-fixture-file count
      (`invalidFiles.size >= 28`, which counts files under `protocol/fixtures/invalid/`, unaffected by
      this in-file vector) still hold; the audio invalid count goes 15 → 16.
      Files: `protocol/fixtures/audio-header.json`
      Verify: `dotnet test plugin/RigPlay.Tests` — the new `pcm-payload-over-8192` case appears in
      `AnInvalidAudioVectorIsRejected` and passes (C# side, build-verified). Kotlin side validated by
      CI; confirm by reading that item 5's guard makes `SimHubAudioCodec.decode` return null for this
      vector.

- [ ] 7. (QW-4, Kotlin) Shut down `teardownExecutor` and `airPlayCommandExecutor` in `onDestroy()`.
      Current behavior: `CarPlayHostActivity.kt` declares the two single-thread executors as fields
      (lines ~348–349). They are shut down ONLY inside the `shutdown(...)` path, and there inside a
      `teardownExecutor.execute { ... }` block (`airPlayCommandExecutor.shutdown()` ~line 3319,
      `teardownExecutor.shutdown()` ~line 3325). `onDestroy()` (lines ~665–682) never shuts them down,
      so on a system-driven recreate where `shutdown()` did not run, both executor threads leak.
      Target behavior: in `onDestroy()`, call `teardownExecutor.shutdown()` and
      `airPlayCommandExecutor.shutdown()` idempotently. `ExecutorService.shutdown()` tolerates being
      called when the executor is already shut down (or when `shutdown()` has already scheduled its own
      shutdown), so no guard is needed; a plain pair of `shutdown()` calls matches the existing pattern
      used in `shutdown()`. Add them in `onDestroy()` (e.g. right before `super.onDestroy()`), after the
      existing `mainHandler.removeCallbacks(...)` cleanup. Do not use `shutdownNow()` (the existing path
      uses `shutdown()`), and do not add blocking `awaitTermination` on the main thread.
      Files: `common/src/main/java/com/shilapi/xcertplay/CarPlayHostActivity.kt`
      Verify: Kotlin not compilable locally — review that both `shutdown()` calls are present in
      `onDestroy()` and are safe when `shutdown()` has already run (idempotent). Validated by CI
      (`:common:testDebugUnitTest`, `:mobile:lintDebug`, `:mobile:assembleDebug`).

- [ ] 8. (QW-5, Kotlin) Cancel the delayed `toneStop` Runnable and release `testToneTrack` in the lifecycle.
      Current behavior: `RigPlayActivity.kt` `playTestTone(streamType)` (lines ~1374–1406) posts a
      delayed `toneStop` Runnable (`handler.postDelayed(stop, 4500)`) that stops/releases the
      `testToneTrack` AudioTrack after ~4.5s. The head of `playTestTone` already cleans up any prior
      tone (removes the pending `toneStop` callback, stops/releases `testToneTrack`, nulls both). But
      nothing cleans up on pause/stop, so leaving the screen while a tone plays leaks the AudioTrack
      and keeps a pending main-thread callback for up to ~4.5s.
      Target behavior: extract the existing head-of-`playTestTone` cleanup into a private helper
      `stopTestTone()` that: `toneStop?.let { handler.removeCallbacks(it) }; toneStop = null;
      testToneTrack?.let { runCatching { it.stop(); it.release() } }; testToneTrack = null`. Call
      `stopTestTone()` from the start of `playTestTone(...)` (replacing the inline cleanup, preserving
      behavior) and from `onPause()` (the existing override at line ~140), before `super.onPause()`
      (place it alongside the existing `handler.removeCallbacks(tick)` cleanup). onPause is the right
      hook: it runs whenever the activity leaves the foreground, bounding the leak to the visible
      session. Keep the field declarations (`testToneTrack: AudioTrack?`, `toneStop: Runnable?`,
      `handler`) unchanged. Make the helper tolerant of being called with nothing active (the `?.let`
      guards already handle null).
      Files: `common/src/main/java/com/shilapi/xcertplay/RigPlayActivity.kt`
      Verify: Kotlin not compilable locally — review that `stopTestTone()` is called from both
      `playTestTone` and `onPause`, and that `playTestTone`'s original inline cleanup is replaced by the
      helper (no double-release). Validated by CI (`:common:testDebugUnitTest`,
      `:mobile:lintDebug`, `:mobile:assembleDebug`).

- [ ] 9. Final verification (build-verified portion).
      From the worktree root run `dotnet test plugin/RigPlay.Tests` and
      `dotnet build plugin/RigPlay -c Release`. All tests MUST pass and the count MUST be ≥ 681 plus
      the new ones added in items 4 and 6 (the new beacon/welcome hostId rows/facts and the
      `pcm-payload-over-8192` fixture case). The Release build MUST succeed. The Kotlin changes
      (items 1, 2, 5, 7, 8) and the Kotlin half of item 6 are "non compilé localement, à valider par la
      CI (`.github/workflows/ci.yml`)" and MUST NOT be reported as build-verified.
      Files: none (verification only).
      Verify: both dotnet commands succeed as described above.

## Assumptions / notes

- Branch `chore/audit-quick-wins` already exists in the worktree (HEAD confirmed); no branch creation
  needed.
- For QW-3 the fixture hex payload is large (~16 KB of hex). This is the mechanism the fixtures use
  (`datagramHex`); the implementer must generate the exact bytes rather than leave a placeholder. An
  all-`00` payload is fine — the vector tests the size cap, not the sample values.
- The strict shared `Fields` accessors in `SimHubProtocol.kt` are used by many decoders; QW-1 must add
  leniency LOCAL to the status/nav decode and must NOT loosen those shared accessors.
