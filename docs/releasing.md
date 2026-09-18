# Release procedure

1. Update `version.json`, `CHANGELOG.md` and the bilingual release note at `docs/releases/{version}.md`. Use a unique SemVer tag, such as `v0.1.0-beta.1`; never replace an already distributed tag with different binaries. The release-note file covers one version only and is the source for the published GitHub release notes.
2. Run `build.ps1`, `test.ps1` and `package.ps1` on Windows. Check the real interface in several languages, including long translations and CJK text. Without an override, `package.ps1` downloads the pinned Inno Setup 6.7.3 compiler, verifies its SHA-256, and extracts it in portable mode under `artifacts/tools`; it does not search for or modify a global Inno Setup installation. Pass an explicit `ISCC.exe` path with `-InnoCompiler` only when using a separately prepared compiler.
3. Confirm that the portable ZIP contains only the two EXEs and allowed documentation. It must not contain `data/`, settings, logs or recovery files. The default package output also includes `UPSGuardian-{version}-windows-x64-setup.exe`, `SHA256SUMS.txt` and `RELEASE_NOTES.md`; compare the generated release notes with `docs/releases/{version}.md`.
4. Run `test-installer.ps1` against an independent sandbox. Check fresh installation, same-directory upgrade, optional shortcut behavior, uninstall with `data/` present and the fact that a different installation directory does not import the old `data/` automatically. Also check that enabled login-at-startup must be disabled and saved before uninstall, and that a pending `recovery.xml` blocks installation and uninstall until recovery completes. Do not use a real user installation or real protection state for this check.
5. If donation resources are included, use the maintainer-approved PNGs without altering their QR payload. Numbered repository secrets `DONATION_WECHAT_PNG_1` through `_6` and `DONATION_ALIPAY_PNG_1` through `_6` contain base64 build-input chunks (at most 40,000 characters each); they are never printed to build logs or committed.
6. Push the tag. The release workflow builds, tests, packages and publishes `UPSGuardian-{version}-windows-x64-setup.exe`, `UPSGuardian-{version}-windows-x64.zip`, `SHA256SUMS.txt` and `RELEASE_NOTES.md`. The workflow must use `docs/releases/{version}.md` as the release-note source.
7. Verify the published asset names, checksums, prerelease flag and update-feed behavior. Check updates from the matching installed channel, and confirm that the ZIP update path and installer path retain `data/` according to the documented rules.

## Installation and data boundaries

The Inno Setup package is a user-level installation with the default location `%LOCALAPPDATA%\Programs\UPS Guardian`. It may create a Start Menu entry and an optional desktop shortcut. A same-directory installer upgrade keeps the installation's `data/` folder; installing into a different directory does not migrate the old folder. Before uninstall, disable and save login-at-startup in the application. A pending `recovery.xml` blocks installation and uninstall until recovery completes. Uninstall leaves `data/` for backup or deliberate cleanup.

The built-in updater continues to consume the portable ZIP, including when the current copy came from the installer. For an in-app update, pause protection and restore limits owned by the application; the updater waits for the application to exit and restarts it after installation. Before running the installer or uninstaller directly, also exit from the system tray. If a recovery record exists, keep it until restoration completes; the installer and uninstaller block while recovery is pending.

## Hardware acceptance

Before claiming support for a hardware combination, separately verify CPU/GPU limit application, readback, restoration, external-setting conflict behavior and session protection on that actual system. A real hibernation test interrupts the current desktop session and must be scheduled with the operator. Beta release notes must distinguish simulated checks from hardware validation.

## macOS

The core build is checked separately to keep Windows dependencies out of shared code. A future Mac release needs its own UI, system power adapter, package verification/install path, signing/notarization and real-device acceptance. Do not rename a Windows ZIP or advertise Windows power settings as a Mac implementation.
