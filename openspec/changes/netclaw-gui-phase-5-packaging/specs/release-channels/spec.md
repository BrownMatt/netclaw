## ADDED Requirements

### Requirement: GUI component publishing

The release pipeline SHALL publish the GUI as a third component named
`netclaw-gui`, built with the same self-contained single-file flags as
the CLI and the daemon. The pipeline SHALL publish the GUI for `win-x64`
in this phase and SHALL carry a per-platform switch so another platform
can be enabled without a change to the packaging steps. The GUI archive
SHALL follow the existing archive name convention
(`netclaw-gui-<version>-<rid>.<ext>`), SHALL appear in the per-platform
checksum file, SHALL attach to the GitHub release, and SHALL upload to
the release feed next to the other archives. The Docker image SHALL NOT
include the GUI.

#### Scenario: Windows release carries the GUI archive

- **GIVEN** a release tag is pushed
- **WHEN** the `win-x64` matrix leg completes
- **THEN** the release assets include `netclaw-gui-<version>-win-x64.zip`
- **AND** `checksums-win-x64.txt` lists that archive with its SHA-256 and size
- **AND** the archive is uploaded to the release feed under `<version>/`

#### Scenario: Platforms without the switch publish no GUI

- **GIVEN** a release tag is pushed
- **WHEN** a matrix leg whose GUI switch is off completes
- **THEN** its assets and checksum file contain only `netclaw` and `netclawd`
- **AND** the leg does not fail for the absent GUI

### Requirement: Manifest component set is closed

Every asset in the release feed manifest SHALL carry a `component` value
from the closed set `netclaw`, `netclawd`, `netclaw-gui`. The manifest
generator SHALL derive the component from the archive name by the
longest matching known name, so `netclaw-gui-…` maps to `netclaw-gui`
and not to `netclaw`. The generator SHALL fail with an error when an
archive name maps to no known component; it SHALL NOT emit a manifest
that carries an unknown component.

#### Scenario: GUI archive maps to its own component

- **GIVEN** a checksum line for `netclaw-gui-0.28.0-win-x64.zip`
- **WHEN** the manifest is generated for `0.28.0`
- **THEN** the asset's `component` is `netclaw-gui` and its `rid` is `win-x64`

#### Scenario: Unknown archive name fails generation

- **GIVEN** a checksum line for `netclaw-extra-0.28.0-win-x64.zip`
- **WHEN** the manifest is generated for `0.28.0`
- **THEN** the generator exits non-zero with an error that names the file
- **AND** no manifest is written

### Requirement: Installer GUI component is opt-in

The install scripts SHALL accept `gui` as a component choice
(`install.sh gui`, `install.ps1 -Component gui`). The default `all`
SHALL keep its meaning of the core set — `netclaw` and `netclawd` — and
SHALL NOT install the GUI. When the operator selects `gui` on a platform
whose release carries no GUI asset, the installer SHALL fail loudly with
an error that names the platform and SHALL NOT report success.

#### Scenario: Default install stays core only

- **WHEN** the installer runs with no component argument on `win-x64`
- **THEN** it installs `netclaw` and `netclawd`
- **AND** it does not install `netclaw-gui`

#### Scenario: Explicit GUI install on Windows

- **WHEN** the installer runs with the `gui` component on `win-x64`
- **THEN** it downloads `netclaw-gui-<version>-win-x64.zip`, verifies its
  checksum, and installs `netclaw-gui.exe` into the install directory

#### Scenario: GUI requested where none is published

- **WHEN** the installer runs with the `gui` component on a platform whose
  release has no GUI asset
- **THEN** the installer exits non-zero with an error that names the platform
- **AND** it installs nothing for that component

### Requirement: Self-update preserves the installed component set

`netclaw update` SHALL always install the core components for the host
platform. It SHALL install an optional component — `netclaw-gui` — only
when a binary of that name is already present in the install directory.
The pre-update summary SHALL list only the components the update will
install. This policy SHALL NOT change whether an update is reported as
available.

#### Scenario: Host without the GUI stays without it

- **GIVEN** the manifest's target release carries `netclaw`, `netclawd`, and
  `netclaw-gui` for the host platform
- **AND** the install directory holds no `netclaw-gui` binary
- **WHEN** the operator runs `netclaw update`
- **THEN** the update installs `netclaw` and `netclawd` only
- **AND** the summary lists two components

#### Scenario: Host with the GUI updates it

- **GIVEN** the same manifest
- **AND** the install directory holds a `netclaw-gui` binary
- **WHEN** the operator runs `netclaw update`
- **THEN** the update installs all three components

#### Scenario: Availability is independent of the policy

- **GIVEN** a newer release whose only host-platform asset is `netclaw-gui`
- **WHEN** the update check runs
- **THEN** the update is still reported as available
- **AND** `netclaw update` installs nothing for the GUI when it is not present
