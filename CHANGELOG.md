# Changelog

## 0.1.0-beta.5

### English

- Add a user-level Windows installer with a default per-user location, Start Menu entry and optional desktop shortcut.
- Keep the portable ZIP available. In-app updates continue to use the ZIP package, while same-directory installer upgrades retain the installation's `data/` folder.
- Preserve `data/` during uninstall and document the boundary between portable updates, installer upgrades and separate installation directories.
- Publish bilingual release notes and installer validation guidance.

**Installing and upgrading:** The installer can be used for a fresh install or an upgrade into the same directory. Installing into a different directory does not migrate the old `data/` folder automatically. Before installing or uninstalling, pause protection, restore any owned limits and exit the application from the tray. If login-at-startup is enabled, turn it off in the application and save before uninstalling. Installation and uninstall are blocked while `recovery.xml` is present, until the application completes recovery. The installer does not certify hardware power changes or hibernation, and this beta makes no code-signing or hardware-compatibility claim.

Validation covers simulated application/update behavior and an isolated installer sandbox. Actual CPU/GPU power changes and hibernation still require controlled validation on the target Windows system.

### 简体中文

- 新增 Windows 用户级安装包：默认安装到当前用户的 `%LOCALAPPDATA%\Programs\UPS Guardian`，可创建开始菜单项和可选的桌面快捷方式。
- 继续提供便携 ZIP；应用内更新仍下载 ZIP。同一安装目录覆盖安装时保留该目录的 `data/`。
- 卸载时保留 `data/`，并补充便携更新、安装包升级和不同安装目录之间的边界说明。
- 发布中英文双语说明，并补充安装包验证说明。

**安装与升级：** 安装包可用于首次安装，也可覆盖安装到原目录。安装到不同目录不会自动迁移旧目录的 `data/`。安装或卸载前，请先暂停保护、恢复程序拥有的限制，并从系统托盘退出应用。如果启用了登录自启，请先在应用内关闭并保存，再进行卸载。检测到 `recovery.xml` 时，安装和卸载会被阻止，必须先完成恢复。本测试版不对实际限功耗、休眠、代码签名或硬件兼容性作出承诺。

验证覆盖模拟的应用/更新行为和隔离的安装包沙箱测试。实际 CPU/GPU 限功耗及休眠仍需在目标 Windows 设备上安排受控验证。

## 0.1.0-beta.4

### English

- Highlight the three main protection thresholds and fold detailed settings behind Advanced settings, with visible summaries of CPU/GPU limits, recovery and hibernation timing.
- Collapse successful UPS discovery into a confirmed-device card; changing the view alone does not scan, change targets or alter protection.
- Simplify the overview by combining device information, hiding routine notices and making the load chart optional without discarding its history.

**Upgrading:** Existing UPS selection and protection settings are preserved. High-load CPU/GPU power reduction and restoration remain available. Protection still requires manual activation after startup.

Validation: 113 simulated checks, isolated WinForms tests for folded settings, discovery confirmation, language layout and lifecycle, plus local interface inspection. Actual CPU/GPU power changes and hibernation are not certified by these tests.

### 简体中文

- 突出保护页的负载、电池电量和续航三个主要阈值；CPU/GPU 限制、恢复和休眠时间等详细设置收进「高级设置」，并持续显示实际参数摘要。
- 成功发现并确认 UPS 后收起搜索区，改为显示已确认设备卡片；展开「更换 UPS」本身不会搜索、切换目标或改变保护状态。
- 合并概览中的设备信息，隐藏常规提示，并让负载曲线按需展开；收起曲线不会丢失后台采集的历史样本。

**升级：** 已保存的 UPS 选择和保护参数会保留。高负载时降低 CPU/GPU 功耗以及后续恢复能力继续保留；启动后仍需手动启用保护。

验证包括模拟规则、界面和生命周期检查，以及折叠设置、UPS 确认、语言布局和本地界面检查。实际 CPU/GPU 限功耗和休眠不由这些测试认证。

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
