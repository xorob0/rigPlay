# ADR 0001: The app and the SimHub plugin live in one repository

**Date:** 2026-10-03
**Status:** Accepted

## Context

rigPlay has two halves that only work together: the Android app, forked from DiPlay, and a SimHub
plugin on the Windows PC. They share a wire protocol that will change often while the project is
young. The options were one repository for both, two repositories with the protocol duplicated, or
two repositories plus a third one for the protocol, pulled in as a submodule or package.

The app keeps taking changes from upstream DiPlay, so whatever is added must not make those merges
harder.

## Decision

One repository. The Android app stays at the root, where DiPlay has it, so upstream merges keep
touching the same paths. The plugin is added under `plugin/`: `plugin/RigPlay` for the plugin and
`plugin/RigPlay.Tests` for its tests. The protocol is written once, in `docs/protocol.md`, with
golden samples in `protocol/fixtures/` that the tests of both halves read.

## Consequences

A protocol change, the code on both sides and the fixtures land in one pull request, and a fixture
test fails on whichever side was not updated. Releases can be tagged once for both halves; the
protocol version range, not the release tag, decides whether two builds can talk.

CI runs a Gradle job and a .NET job; each job filters on the paths it owns so a plugin-only change
does not rebuild the app. Upstream DiPlay merges never touch `plugin/` or `protocol/`. The cost is a
repository with two toolchains, and contributors to one half see the other's history.
