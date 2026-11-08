# WP10 Windows alpha acceptance contract

WP10 delivers an installable WYU single-player alpha. A first-time
player can install it as a standard Windows user, learn the controls,
change settings and finish a test mission from the installed app.
The acceptance route builds on the [WP07 rescue beacon](wp07-playable-prototype-contract.md)
and [WP09 flight](../wp09-handover.md). Groundwork owns the engine
packages, runtime diagnostics and Windows lifecycle. WYU owns the
installer, first-run experience, settings and mission progression.

## Release candidate and installer

The proposed alpha target is a per-user Windows 11 x64 installer and a
self-contained `win-x64` Release build. The installer must carry any
redistributable runtime prerequisites needed by the supported Windows
baseline. Installation and play must work offline after the installer
and its verification material have been obtained. A standard user can
install, launch and uninstall without elevation. Launch is through a
Start menu entry, and uninstall is available from Windows Installed apps.
The choice of installer tooling is part of the implementation review.
The deployment and installation models are documented in
[.NET publishing](https://learn.microsoft.com/en-us/dotnet/core/deploying/) and
[Windows installation contexts](https://learn.microsoft.com/en-us/windows/win32/msi/installation-context).

- Build from clean approved commits with a pinned SDK, exact verified
  Groundwork packages and locked dependency restore. Record both
  commits, package version and manifest hash, build run, target runtime,
  bundled runtime version and hashes of the staged payload and final
  installer. Record the installer version and publisher identity in
  the release record. Follow [ADR-0006](../adr/ADR-0006-packaging-release.md)
  for package provenance and retention.
- Distribute the reviewed installer and verification material together
  from durable release storage. For this WP10 alpha, the owner accepts
  an unsigned installer. Verify the final installer digest against the
  reviewed verification material. Acceptance uses ordinary Windows
  protections and the standard-user account throughout.
- Separate installed files from per-user settings, saves and logs under
  local application data. An interrupted or canceled install leaves
  the previous installed build usable, or leaves a first-time user
  without a launch entry. A corrupt installer is rejected. Reinstall
  restores program files and preserves settings and saves. Uninstall
  removes installed files and launch entries and retains user data.

## First launch and settings

Launch the installed client into a menu with **Start test mission**,
**Continue**, **Settings** and **Exit**. Enable Continue when a supported
checkpoint is available. Before replacing an occupied slot, identify
the save and require a separate confirmation. If a checkpoint is
incompatible or unreadable, show the reason and preserve the slot.

First-run guidance teaches movement, mouse look, interaction, the
objective marker and how to open the pause menu and save. Teach each
action where it is first needed and show the current controls on demand.
Explain the guided flight start and the amber landing marker before
departure. Allow skipping and replaying the guidance. Losing focus or
opening the menu releases mouse capture and clears held gameplay input.
Menu navigation, Apply, Cancel and Exit must work with the keyboard.

| Setting | Default | Acceptance |
| --- | --- | --- |
| Windowed size | 960 × 540 | Offer supported sizes that fit the current display. A rejected resize keeps the last usable window and reports the result. |
| Mouse look sensitivity | 1.0× | Allow 0.25× through 3.0× and apply it to gameplay mouse look. |
| Invert vertical look | Off | Apply it to mouse look and show the current value in Settings. |

Apply validates and persists a complete settings snapshot. Cancel
discards edits, and Reset to defaults stages the defaults for Apply.
Settings survive exit and relaunch and are separate from world saves.
A corrupt or unsupported settings file starts with safe defaults and
preserves the original for diagnosis. Interrupted publication leaves
either the last complete settings or the new complete settings.

## Installed test mission

The alpha presents one ordered mission through the installed menus.
The surface rescue and planetary flight use their existing separate
world and save domains. Record mission progression from validated
checkpoints. A retry or interrupted phase transition must not duplicate
rewards, reset a completed stage or advertise completion ahead of the
underlying checkpoint.

| Stage | Player action and required result |
| --- | --- |
| Start and orient | From a fresh profile, start the mission, complete or skip the introduction, and demonstrate move, look, interact and pause. Change a setting before the first objective. |
| Rescue signal | Find the three deposits, mine the stone, return to the marked build site, build three modules and activate the beacon. The objective and visible state advance as each action commits. |
| Save and continue | Save during partial construction, exit to the desktop, relaunch from Start and select Continue. Restore the terrain, inventory, build progress and applied settings. Complete the signal and present the next mission stage. |
| Flight and landing | Start the guided flight at the edited pad. Complete takeoff, insertion, a full orbit, return and touchdown within the WP09 tolerance. Confirm the visible amber landing marker on approach and save the landed checkpoint. |
| Finish and restart | Show an explicit mission completion state. Exit and reopen the installed app in a fresh process. Continue restores the landed pose, canonical edits and completed mission state. |

The user-facing run uses ordinary installed menus and controls. A
separate deterministic replay exercises the same application paths and
records semantic checkpoints, exact input trace identities and the
expected saved state. Compare checkpoint semantics across two replays.
Record loading waits and frame timings separately.

## Clean-system release gates

Use a freshly provisioned Windows 11 x64 profile on local NTFS with
normal OS protections, no prior WYU data and no SDK, developer checkout,
NuGet cache or separately installed .NET runtime required by WYU.
Record the exact OS edition and build, CPU, RAM, adapter, driver and
display mode. The reference hardware run uses the NVIDIA D3D12 setup
qualified in WP09. Publish supported configurations and known
limitations from measured results. WARP and the separate hardware run
with Debug Layer and DRED remain renderer qualification gates. The
ordinary installed launch must work without Graphics Tools.

| Gate | Evidence required |
| --- | --- |
| Installation and cleanup | Verify installer provenance and test standard-user install, Start menu launch, cancel, corrupt package rejection, reinstall and uninstall. Verify user data preservation and program-file cleanup. |
| First-run and settings | Walk the keyboard and mouse flows from a fresh profile. Verify Apply, Cancel, reset, relaunch, focus loss and corrupt settings recovery. Record the initial screen and how the next objective becomes visible. |
| Mission and recovery | Complete the installed route and its two deterministic replays. Verify a clean-process resume at partial build and after landing. Exercise interrupted save and protect an incompatible occupied slot. |
| Runtime and diagnostics | Exercise normal exit and close during loading. Record actionable startup and graphics failures, bounded local logs and clean shutdown. Require zero live GPU child objects after the diagnostic hardware run. Include build identity and phase in diagnostics. |
| Acceptance record | Link the exact installer digest, Groundwork and WYU heads, CI, replay traces and clean-system evidence. Record findings and limitations for owner acceptance before distributing the alpha. |

Implement in reviewable stages. Start with verified Windows packaging
and installation smoke, then first-run navigation and settings, then
mission progression and the clean-system release qualification.
