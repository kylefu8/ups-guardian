# Platform boundaries

This release keeps the working Windows interface and separates reusable code from operating-system actions. The macOS application is a later phase; no macOS binary or unsupported power-control capability is claimed by this release.

| Area | Location | Responsibility |
| --- | --- | --- |
| Shared logic | `source/Core` | NUT protocol, UPS data, protection decisions, localization, release metadata, update validation, platform contracts |
| Windows application | `source/Windows` | WinForms interface, tray, Windows power settings, NVIDIA control, startup registration, update hand-off |
| Update helper | `source/Updater` | Wait for the app to close, validate and install an update, rollback on failure |
| Language resources | `locales` | Simplified Chinese, English, Japanese, Korean, French, German and Spanish |
| Verification | `tests` | Simulated UPS responses, pure rules, fake power interfaces and sandboxed update tests |

## macOS follow-up

The next phase must select and verify a macOS UI, menu-bar lifecycle, signing/notarization and system power adapter on a real Mac. NUT and rule semantics should be shared. Power capabilities must be reported by the platform adapter rather than inferred from Windows behavior: NVIDIA watt caps and Windows hibernation must not be advertised as generic Mac capabilities. The choice of macOS UI framework remains open.

## Configuration and distribution

Runtime data is separate from release files and excluded from Git and release archives. New installations start without automatic protection. Existing installations preserve their saved settings. Release assets are checked against their published SHA-256 sums before installation; application updates never import the maintainer's local UPS address, logs or recovery record.
