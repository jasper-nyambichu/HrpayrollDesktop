# Branch PC installer

## Build it

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

The result is `installer\Output\DhwarshHR-Setup-<version>.exe`. Raise `<Version>` in `HrpayrollDesktop.csproj` before each build you hand out.

You need the .NET 10 SDK and Inno Setup 6 (`winget install JRSoftware.InnoSetup`). The script makes a Release build, copies the SecuGen drivers into `installer\redist` the first time, and downloads the Microsoft Visual C++ runtime.

## Install on a branch PC

1. Copy the setup .exe to the PC and run it. Windows asks for an administrator. Windows may also say "Windows protected your PC", because the installer isn't code-signed. Choose **More info**, then **Run anyway**.
2. Keep **Start automatically when Windows starts** ticked.
3. Plug in the SecuGen reader, before or after installing. The drivers are already installed, and the app finds the reader within a few seconds, with no restart.
4. The Back Office signs in once with the login the Admin gave them. This links the reader to their branch. Until then the home screen shows "Not activated".

Setup installs:

- the app, with its own .NET, in `C:\Program Files\Dhwarsh HR`
- the SecuGen drivers for every reader model
- the Visual C++ runtime, when the PC doesn't have it

Data is kept in `C:\ProgramData\Dhwarsh HR`: unsent scans, the reader's activation and fingerprints. Upgrading or uninstalling leaves it in place. If a reader isn't recognised, check `driver-install.log` in that folder, or use **Reinstall fingerprint reader drivers** in the Start menu.

To upgrade by hand, run the newer setup .exe. It closes the running app first, and nothing saved is lost.

## Automatic updates

From version 1.3.0, every installed PC checks GitHub Releases of `jasper-nyambichu/HrpayrollDesktop` every hour, and five minutes after Windows starts. It installs a newer version by itself, silently, with no administrator prompt. It waits while anyone is signed in to the Back Office console or manual check-in, then opens the app again. PCs on 1.2.0 or older need 1.3.0 installed by hand once.

An update is installed only if it is signed with the release key, so nobody else can push a program onto the branch PCs, even with access to the GitHub account.

**One time, on the PC that builds releases:** run `installer\new-signing-key.ps1`, then commit `installer\updater\update-public-key.xml`. Back up `%USERPROFILE%\.dhwarsh\update-signing-key.xml` somewhere safe and never commit it. Without it, no further automatic updates can be published.

**Each release:**

1. Raise `<Version>` in `HrpayrollDesktop.csproj`, for example 1.3.0 → 1.3.1. Deploy any backend changes first.
2. Run `powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1`. It builds and signs the setup, creating `.exe` and `.exe.sig`.
3. Test the setup on one PC.
4. Run `powershell -ExecutionPolicy Bypass -File installer\publish-release.ps1 -Notes "What changed"`. This needs `gh auth login` with an account that can create releases.

What each PC did is written to `C:\ProgramData\Dhwarsh HR\update.log`, and the last silent install is in `update-install.log`. To check for an update straight away, open Task Scheduler → Dhwarsh HR → Updater → Run.
