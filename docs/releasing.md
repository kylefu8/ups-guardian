# Release procedure

1. Update `version.json` and `CHANGELOG.md`. Use a unique SemVer tag, such as `v0.1.0-beta.1`; never replace an already distributed tag with different binaries.
2. Run `build.ps1`, `test.ps1` and `package.ps1` on Windows. Check the real interface in several languages, including long translations and CJK text.
3. Confirm that the ZIP contains only the two EXEs and allowed documentation. It must not contain `data/`, settings, logs or recovery files.
4. If donation resources are included, use the maintainer-approved PNGs without altering their QR payload. Numbered repository secrets `DONATION_WECHAT_PNG_1` through `_6` and `DONATION_ALIPAY_PNG_1` through `_6` contain base64 build-input chunks (at most 40,000 characters each); they are never printed to build logs or committed.
5. Push the tag. The release workflow builds, tests, packages and publishes `UPSGuardian-{version}-windows-x64.zip` and `SHA256SUMS.txt`.
6. Verify the published asset names, checksum, prerelease flag and update-feed behavior. Check updates from the matching installed channel.

## Hardware acceptance

Before claiming support for a hardware combination, separately verify CPU/GPU limit application, readback, restoration, external-setting conflict behavior and session protection on that actual system. A real hibernation test interrupts the current desktop session and must be scheduled with the operator. Beta release notes must distinguish simulated checks from hardware validation.

## macOS

The core build is checked separately to keep Windows dependencies out of shared code. A future Mac release needs its own UI, system power adapter, package verification/install path, signing/notarization and real-device acceptance. Do not rename a Windows ZIP or advertise Windows power settings as a Mac implementation.
