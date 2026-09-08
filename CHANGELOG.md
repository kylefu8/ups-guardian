# Changelog

## 0.1.0-beta.2

- Fix the disposed Icon exception when restarting as administrator or exiting. The tray and window now finish cleanup before shared images are released, and pending UI callbacks stop at shutdown.
- Move Updates and Support into the Guide, keeping the main navigation focused on monitoring and protection.
- Expand the guide in all seven languages with connection setup, load thresholds, permissions, battery-only hibernation, tray operation, recovery and update instructions.
- Preserve version headings in the built-in release notes.

Validation includes real WinForms close-to-tray, reopen, exit and repeated disposal, queued callbacks, all seven guide languages, and existing simulated protection/update tests. UAC approval and hardware power changes or hibernation are not automated by these checks.

## 0.1.0-beta.1

First public Windows beta.

- UPS monitoring over the NUT protocol, live load history and system-tray operation.
- Configurable local power limits, restoration and battery-only session-protection rules.
- Seven interface languages with in-app language selection.
- Built-in quick guide, version information and GitHub release updates.
- Optional support page for maintainer-provided donation codes.
- Shared protocol, decision, localization and update code separated from Windows integration for a later macOS version.

This is a beta. Hardware power changes and hibernation must be validated on the user's own system. A simulated test does not certify every UPS, GPU driver or Windows power policy.
