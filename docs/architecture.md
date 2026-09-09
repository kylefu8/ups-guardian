# Platform boundaries

This release keeps the working Windows interface and separates reusable code from operating-system actions. The macOS application is a later phase; no macOS binary or unsupported power-control capability is claimed by this release.

| Area | Location | Responsibility |
| --- | --- | --- |
| Shared logic | `source/Core` | NUT protocol and bounded discovery, UPS data, protection decisions, localization, release metadata, update validation, platform contracts |
| Windows application | `source/Windows` | WinForms interface, tray, Windows power settings, NVIDIA control, startup registration, update hand-off |
| Update helper | `source/Updater` | Wait for the app to close, validate and install an update, rollback on failure |
| Language resources | `locales` | Simplified Chinese, English, Japanese, Korean, French, German and Spanish |
| Verification | `tests` | Simulated UPS responses, pure rules, fake power interfaces, sandboxed update tests and isolated real-window lifecycle tests |

## macOS follow-up

The next phase must select and verify a macOS UI, menu-bar lifecycle, signing/notarization and system power adapter on a real Mac. NUT and rule semantics should be shared. Power capabilities must be reported by the platform adapter rather than inferred from Windows behavior: NVIDIA watt caps and Windows hibernation must not be advertised as generic Mac capabilities. The choice of macOS UI framework remains open.

## Configuration and distribution

`NutDiscovery` enumerates NUT devices using `LIST UPS` and accepts candidates only after a readable `LIST VAR` response with UPS status. Scans use a single deadline, cancellation and bounded concurrency. The Windows discovery page never preselects a candidate, and its confirmation button validates the chosen server/device again before persisting the one confirmed target. Scan-port changes affect discovery only; they cannot directly change a monitored target.

Persisted `ConnectionConfirmed` records user selection; transient `connectionReady` records successful validation in the current process. Polling and protection require both. Startup always disarms protection and revalidates a saved target before polling. Legacy profiles default to unconfirmed while retaining their existing settings. Failed validation does not choose a replacement. Connection generations reject results from obsolete scans, validations and polling requests, including reconnects to the same address.

Runtime data is separate from release files and excluded from Git and release archives. New installations start without automatic protection. Existing installations preserve their saved settings. Release assets are checked against their published SHA-256 sums before installation; application updates never import the maintainer's local UPS address, logs or recovery record.

The Windows Form owns its tray, menu and shared graphics. Genuine closure stops polling and queued UI callbacks. Disposal detaches the tray while its icon remains valid, disposes the Form and only then releases owned images and localization fonts. Closing to the tray cancels closure and keeps those resources alive. Guide, Updates and Support are separate top-level sidebar pages; their controls are created once, so navigation does not discard download state.
