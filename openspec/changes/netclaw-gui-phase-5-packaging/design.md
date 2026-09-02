# Design: netclaw-gui-phase-5-packaging

## Context

See `proposal.md` — Why. The relevant current state:

- `scripts/build/publish-binaries.sh` is the single source of truth for
  publish flags. `--component` accepts `cli|daemon|all`. Three callers
  pass `all`: the release workflow, `scripts/docker/build-image.sh`, and
  `scripts/smoke/run-smoke.sh` (also `smoke.yml`).
- `publish_release_binaries.yml` runs one matrix leg per RID. Each leg
  packages two archives, computes one checksum file, uploads archives to
  the GitHub release (`archives/*`), and uploads each archive to R2 with
  one explicit step per component.
- `feeds/scripts/generate-release-manifest.sh` derives the component from
  the archive name with `sed -E "s/^(netclawd?)-${VERSION}-.*/\1/"`. A
  name with another prefix falls through unchanged, so the manifest would
  carry the whole file name as the component. That is a silent fallback.
- `install.sh` / `install.ps1` accept `all|cli|daemon`; `all` installs
  `netclaw` and `netclawd`. The install smoke harnesses
  (`scripts/smoke/install-smoke.sh` / `.ps1`) build a fake feed with the
  real manifest generator and drive the real installers.
- `UpdateCheckService.EvaluateManifest` returns every asset for the host
  RID. `UpdateCommand.PerformUpdateAsync` downloads and installs every
  returned asset. Component names are string literals in the scripts and
  tests; no C# constant set exists.
- `Netclaw.Gui.csproj` has no `AssemblyName`, so the binary is
  `Netclaw.Gui.exe`. `deploy-local.ps1` mirrors the publish flags for
  `cli` and `daemon` only.
- The user chose `win-x64` as the only GUI platform for this phase.

## Goals / Non-Goals

**Goals:**

- One component name, `netclaw-gui`, used identically by the publish
  script, the archive name, the checksum line, the manifest, the
  installers, and `netclaw update`.
- No change in build cost or output for Docker and the smoke harness.
- A per-RID switch in the release matrix, so a later platform is one
  matrix line plus nothing else.
- The local deploy proves the single-file publish on the target platform.

**Non-Goals:**

- GUI publish for any RID other than `win-x64`.
- A GUI-side update check or self-update.
- Signing, notarization, or an installer package (MSI, `.app`).

## Decisions

### D1 — Component name `netclaw-gui`; assembly name set in the csproj

The binary is `netclaw-gui` (`netclaw-gui.exe` on Windows), set with
`<AssemblyName>netclaw-gui</AssemblyName>`. The name follows the
existing pattern where the component name equals the binary name
(`netclaw`, `netclawd`), which both installers and `UpdateCommand` rely
on (`$"{component}.exe"`). The process name changes from `Netclaw.Gui`
to `netclaw-gui`; `deploy-local.ps1` and the vault docs update to match.

Alternative: keep `Netclaw.Gui.exe` and map component → file name in
every consumer. Rejected: it adds a mapping table to four scripts and
one command for no user benefit.

### D2 — `publish-binaries.sh` gains `gui` and `core`; `all` means all

`--component` accepts `cli|daemon|gui|core|all`. `core` = cli + daemon.
`all` = cli + daemon + gui. Docker, `run-smoke.sh`, and `smoke.yml`
switch to `core`, so their behavior and cost do not change. The release
workflow passes `all` on a leg whose switch is on and `core` otherwise.

Alternative: keep `all` = cli + daemon and call the script twice on the
release leg. Rejected: `all` would then not mean all, and the Docker
and smoke scripts would carry an implicit exclusion nobody can see.

Alternative: a comma-separated list. Rejected: more parsing for one
extra name; `core` says what the callers mean.

### D3 — Release matrix carries a `gui` boolean per RID

The `include` matrix gains `gui: true` on `win-x64` and `gui: false` on
the other three legs. Steps that touch the GUI use `if: matrix.gui`:
the Windows package step adds the GUI archive, and one new R2 upload
step uploads it. The checksum step already iterates `archives/*` and the
GitHub upload already globs `archives/*`, so neither changes. The
ARM64 verification and the downstream publish-output artifact stay on
cli + daemon.

The `Package archives (Unix)` step gains the matching GUI branch guarded
by `matrix.gui`, so a later Linux or macOS switch needs no packaging
edit. That is the whole cost of the per-RID switch and it is small.

### D4 — Manifest generator maps by longest known name and fails on unknown

Replace the `sed` derivation with a `case` on the file name against the
closed set, longest name first: `netclaw-gui-${VERSION}-*`,
`netclawd-${VERSION}-*`, `netclaw-${VERSION}-*`. Any other name is an
error that names the file and exits non-zero before the manifest is
written. This removes the silent fallback that exists today and
guarantees `netclaw-gui-…` never maps to `netclaw`.

The install smoke harnesses add a `netclaw-gui` stand-in archive on one
fixture platform only. The PowerShell harness (`win-x64`) puts it in the
stable release entry and not the prerelease one; the bash harness
(Unix RIDs) puts it in the `linux-x64` checksum file only. The fixture
platform is a harness choice, not the release policy: it lets the real
installers prove the opt-in path, the `all` exclusion, and the "GUI
requested where none is published" error with one feed. The bash
harness also runs the real generator against a bogus archive name and
asserts it fails before a manifest exists.

Found while running the PowerShell harness on the developer machine:
`Start-Process -ArgumentList` does not quote an array element that
contains a space, so the `--directory` path for the fixture server split
in two when the temp path contained one. The harness now quotes that
argument. CI runners have space-free temp paths and never hit it.

### D5 — Installers: `gui` opt-in, `all` stays core

`install.sh` accepts `gui` in its component argument; `install.ps1`
adds `gui` to the `ValidateSet`. `all` is unchanged. Rationale: the
default install target is a headless host (the daemon's home); a GUI
binary there is dead weight, and the `all` default is what every
existing runbook and the Docker entrypoint assume.

The "no asset" path already exists in both scripts ("No $component
binary found for $RID"). It exits non-zero. The change only makes the
message name the platform so an operator on Linux understands why.

### D6 — Self-update installs core always, optional only when present

Add `BinaryComponents` to `Netclaw.Configuration.Feeds`:

```csharp
public static class BinaryComponents
{
    public const string Cli = "netclaw";
    public const string Daemon = "netclawd";
    public const string Gui = "netclaw-gui";
    public static IReadOnlyList<string> Core { get; } = [Cli, Daemon];
    public static bool IsOptional(string component) => component == Gui;
}
```

`UpdateCommand` gains a pure `SelectAssetsToInstall(assets, installDir,
isWindows)` that keeps core assets and keeps an optional asset only when
`<component>[.exe]` exists in `installDir`. The confirm prompt and
`PerformUpdateAsync` consume the selected list. `EvaluateManifest` is
untouched, so `IsUpdateAvailable`, the daemon alert, `netclaw status`,
and `netclaw doctor` keep their behavior (spec scenario "Availability is
independent of the policy").

Alternative: filter in `EvaluateManifest`. Rejected: that service is
shared with the daemon and has no install directory; the policy belongs
to the command that writes files.

Alternative: install the GUI everywhere on Windows. Rejected: an update
must never widen the install footprint without an operator choice.

Ownership: the decision is call-local to `UpdateCommand`; the only
durable effect is the files it writes to the install directory, which is
already its contract.

Failure modes: if the GUI download or swap fails, the existing per-asset
rollback applies (the `.backup` swap). A failure on the GUI asset leaves
the core binaries untouched only if the GUI is processed last, so the
selection orders core first, optional last.

### D7 — `deploy-local.ps1` gains `gui`; `all` includes it on this host

The local deploy is the Windows single-file proof path. `-Component`
accepts `gui`; `all` publishes and installs all three, kills a running
`netclaw-gui` before the copy, and reports the installed GUI path. This
is the E2E check for "single-file publish with Avalonia native
libraries on win-x64" (task 5.2): launch the installed
`netclaw-gui.exe`, attach to the daemon, send one message, see the
reply.

## Risks / Trade-offs

- [Avalonia native libraries fail to load from the single-file
  self-extract] → `IncludeNativeLibrariesForSelfExtract=true` is already
  in the common flags; the local deploy launch is the proof. If it fails,
  the fix is in the publish flags for the `gui` component only, not in
  the shared `COMMON` array.
- [`EnableCompressionInSingleFile=true` slows GUI first start] → the
  extract happens once per version into the user's temp; acceptable for
  a desktop app. Measured on the local deploy; recorded in the vault doc.
- [Windows SmartScreen warns on an unsigned GUI exe] → same posture as
  the unsigned CLI today; signing is out of scope and noted in the
  proposal.
- [Assembly rename breaks a script that expects `Netclaw.Gui.exe`] →
  only the avalonia-drive smoke notes and the vault docs reference the
  old name; both update in this change.
- [Docker or smoke picks up the GUI by accident] → they switch to
  `core` in the same change; the publish script rejects an unknown
  component, so a typo fails loudly.
- [Manifest generator now fails on an unexpected file in `checksums/`] →
  intended. The checksum step writes only what the package steps
  produced; an unexpected file is a pipeline bug and must stop the
  release.

## Migration Plan

No data migration. Existing manifests stay valid: `component` values
already in the feed are inside the closed set. Older CLIs ignore the
`netclaw-gui` asset on the pre-update summary only by installing it
(their current behavior), which is why the policy in D6 ships before the
first release that carries a GUI asset. Rollback is a revert; no
published artifact needs removal.
