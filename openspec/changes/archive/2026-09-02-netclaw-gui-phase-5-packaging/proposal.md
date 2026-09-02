# Proposal: netclaw-gui-phase-5-packaging

## Why

The GUI is complete (Phases 0–4) but it does not ship. The release
pipeline publishes two components, `netclaw` and `netclawd`; the GUI only
exists as a local Release build. GUI roadmap Phase 5
(`docs/netclaw/plans/netclaw-gui-plan.md`) makes packaging the last
deliverable: add the `gui` component to the publish script, the release
workflow, the feed manifest, and the component set that installers and
`netclaw update` understand.

## What Changes

- Publish the GUI as a self-contained single-file binary named
  `netclaw-gui` with the same canonical publish flags as the CLI and the
  daemon. The release ships it for `win-x64` only in this phase.
- Add the `netclaw-gui` component to the feed manifest. The manifest
  generator maps every archive name to a known component and fails on
  an archive it cannot classify.
- Installers: `gui` becomes an explicit component choice. The default
  `all` keeps its meaning of CLI plus daemon, because the default target
  is a headless host.
- Self-update: `netclaw update` always installs the core components and
  installs `netclaw-gui` only when that binary is already present in the
  install directory. Today it installs every asset for the host RID, so
  a GUI in the manifest would land on every updated host.
- Local deploy: `scripts/deploy-local.ps1` gains the `gui` component, so
  the local install path proves the single-file publish end to end.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `release-channels`: add the GUI component to publishing, the
  manifest component set, installer component selection, and the
  self-update component policy.

## Impact

- **PRD linkage:** no PRD requirement covers GUI distribution. The
  `release-channels` spec deltas in this change are the authority
  (same posture as Phases 3 and 4). PRD-004 (CLI onboarding) owns
  installer behavior in general; this change does not alter its
  fresh-install or existing-install flows.
- **In scope (MVP):** `win-x64` GUI publish, archive, checksum, GitHub
  release asset, R2 upload, manifest entry, installer opt-in,
  self-update policy, local deploy, release documentation.
- **Out of scope:** GUI binaries for `linux-x64`, `linux-arm64`, and
  `osx-arm64` (the matrix carries a per-RID switch so a later change can
  turn one on); a macOS `.app` bundle; code signing or notarization;
  a GUI-side update check; Docker image contents (the image stays CLI
  plus daemon).
- **Code:** `Netclaw.Gui.csproj` (assembly name), `publish-binaries.sh`
  (component set), `publish_release_binaries.yml` (matrix switch,
  package and upload steps), `generate-release-manifest.sh` (strict
  component mapping), `install.sh` / `install.ps1` (component choice),
  `UpdateCommand` (asset selection) with a shared component-name
  constant set in `Netclaw.Configuration.Feeds`, `deploy-local.ps1`,
  the install smoke harnesses, `CONTRIBUTING.md`.
- **Security:** the GUI archive rides the same signed manifest and
  SHA-256 checksum path as the other components. No new download source
  and no new trust anchor. The self-update policy narrows what an update
  writes to the install directory; it never widens it.
- **Operations:** operators on Windows can install the GUI with one
  installer flag and keep it current with `netclaw update`. Docker and
  the smoke harness keep their build cost unchanged because they publish
  the core set only.
