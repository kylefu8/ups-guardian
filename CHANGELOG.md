# Changelog

## 0.1.0-beta.3

- Replace the language dropdown with a compact globe menu and show the current version persistently beside it, with a shortcut to Updates.
- Expose Updates and Support directly in the main sidebar, while keeping the Guide focused on operating instructions and retaining update state across navigation.
- Give Activity log, Guide, Updates and Support distinct icons, while retaining the approved donation resources in release builds.
- Discover readable NUT UPS devices on the local IPv4 network, choose exactly one and explicitly confirm it before monitoring. Remembered targets are revalidated on startup; automatic protection always requires manual activation.
- Discard cached UPS readings and pending results when the connection changes or reconnects, so protection waits for data from the current connection.
- Attempt CPU and GPU recovery independently, retaining the recovery record when either operation fails so a later retry can finish.
- Reopen the intact previous installation if update validation, extraction or backup fails before file replacement; retain the original failure in the update log.

**Upgrading:** Existing users must discover and confirm their UPS once after upgrading. Saved thresholds and other protection parameters are preserved. Subsequent launches verify the remembered target before monitoring; protection always stays off until manually enabled. Discovery currently covers local IPv4 networks, with a displayed scope, cancellation and bounded scanning.

Validation: 113 simulated policy, localization, NUT, discovery, power-action and update checks, plus isolated WinForms tests for language selection, seven sidebar pages, retained update state, connection confirmation and lifecycle handling. LAN discovery and the real interface were also checked locally. Actual CPU/GPU power changes, UAC approval and hibernation are not certified by these tests.

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
