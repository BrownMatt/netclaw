# Tasks: netclaw-gui-phase-5-packaging

## 1. Component identity (D1, D6)

- [x] 1.1 Set `<AssemblyName>netclaw-gui</AssemblyName>` in
  `Netclaw.Gui.csproj`. Verify: `dotnet build -c Release` emits
  `netclaw-gui.exe` and `Netclaw.Gui.Tests` still passes.
- [x] 1.2 Add `BinaryComponents` (`Cli`, `Daemon`, `Gui`, `Core`,
  `IsOptional`) in `Netclaw.Configuration.Feeds` and use the constants
  in `UpdateCommand` and its tests. Verify: build is green with no
  remaining `"netclaw"` / `"netclawd"` literals in `UpdateCommand`.

## 2. Publish script and release workflow (D2, D3)

- [x] 2.1 Extend `publish-binaries.sh`: `--component cli|daemon|gui|core|all`,
  `core` = cli + daemon, `all` = all three; update the usage header.
  Verify: `bash scripts/build/publish-binaries.sh --rid win-x64 --component gui`
  produces `publish/gui/netclaw-gui.exe`; an unknown component exits 2.
- [x] 2.2 Switch `scripts/docker/build-image.sh`, `scripts/smoke/run-smoke.sh`,
  and `.github/workflows/smoke.yml` to `--component core`. Verify: the
  three call sites pass `core` and the Docker script's binary checks are
  unchanged.
- [x] 2.3 Release workflow: add `gui: true|false` to each matrix leg
  (`win-x64` on), pass `all` or `core` from the switch, add the GUI
  branch to both package steps guarded by `matrix.gui`, and add the GUI
  R2 upload step. Verify: `actionlint` (or the workflow's YAML parse)
  passes and every GUI-touching step carries `if: matrix.gui`.

## 3. Manifest generator and installers (D4, D5)

- [x] 3.1 Replace the `sed` component derivation in
  `generate-release-manifest.sh` with a longest-name-first `case` over
  the closed set; an unknown name exits non-zero before the manifest is
  written. Verify: a checksum line for `netclaw-gui-…` yields
  `"component": "netclaw-gui"`; a line for `netclaw-extra-…` fails with
  the file name in the error and leaves no manifest.
- [x] 3.2 Installers: add `gui` to `install.sh` component parsing and
  `install.ps1` `ValidateSet`; `all` stays core; the missing-asset error
  names the platform. Verify: dry-run with `gui` on `win-x64` prints the
  GUI DRY RUN line; dry-run with `all` prints no GUI line.
- [x] 3.3 Install smoke harnesses: add the `netclaw-gui` stand-in archive
  on one fixture platform (`win-x64` stable entry in the PowerShell
  harness, `linux-x64` checksum file in the bash harness), and add checks
  for the opt-in install, the `all` exclusion, and the "no GUI published"
  error. Verify: `bash scripts/smoke/install-smoke.sh`
  and `./scripts/smoke/install-smoke.ps1` pass locally.

## 4. Self-update policy (D6)

- [x] 4.1 Add `SelectAssetsToInstall` to `UpdateCommand` (core always,
  optional only when present, core ordered first) and route the confirm
  summary and `PerformUpdateAsync` through it. Verify with
  `UpdateCommandTests`: GUI absent → two assets; GUI present → three
  assets with the GUI last; `EvaluateManifest` still reports the update
  when the GUI is the only host asset.

## 5. Local deploy, verification, and docs (D7)

- [x] 5.1 `deploy-local.ps1`: add `gui` to `-Component`, include it in
  `all`, kill a running `netclaw-gui`, copy `netclaw-gui.exe`, print the
  installed path. Verify: `pwsh -File scripts/deploy-local.ps1 -Component gui -NoRestart`
  installs `%LOCALAPPDATA%\Programs\netclaw\netclaw-gui.exe`.
- [x] 5.2 E2E: launch the installed `netclaw-gui.exe`, confirm it
  attaches to the running daemon, send one message, see the reply, note
  first-start time. Record the result and the single-file finding in the
  vault (`docs/netclaw/gui-phase-5.md`) and link it from `docs/README.md`.
- [x] 5.3 Run the gates: `dotnet build` (0 warnings), full test suite,
  `dotnet slopwatch analyze`, `./scripts/Add-FileHeaders.ps1 -Verify`.
- [x] 5.4 Docs: `CONTRIBUTING.md` Releasing gains the component list and
  the per-RID GUI switch; `docs/netclaw/local-install.md` gains the GUI
  install line; `netclaw-operations` `references/diagnostics.md` notes
  the `netclaw update` optional-component rule and the skill version is
  bumped. Verify: each file shows the new text.
