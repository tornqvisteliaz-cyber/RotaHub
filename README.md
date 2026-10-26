# RotaHub

Rotaidem's add-on manager for Microsoft Flight Simulator, in the style of iniManager and Aerosoft One.
Log in with your rotaidem.com account, see every Rotaidem product, install, update, repair and
uninstall them, and manage your licence keys.

- **Same account as the website.** RotaHub logs in through `/api/login` like a browser; the login
  shows on rotaidem.com under the account's logins as "RotaHub on Windows" and can be ended there.
  The session token is kept encrypted for the Windows user (DPAPI).
- **Products come from the website** (`/api/products`, Admin → Products): name, pictures, tagline,
  description, features and version history. Drafts are hidden.
- **Licence keys come from the account** (`/api/account/licenses`). RotaHub activates a key on the PC
  exactly like the product's own installer (same machine id, same `%LOCALAPPDATA%\Rotaidem\{product}\license.json`),
  so an installed product starts already activated. *Add a key* attaches a key to the account
  (`/api/account/licenses/claim`) and activates it.
- **The website admin drives installs and updates.** A product's download link and version in
  Admin → Products are enough: RotaHub installs it (installer, MSFS package zip or plain zip) and
  offers an update when the version goes up. It checks every 5 minutes and when its window comes
  back, shows a Windows notification, and installs zip updates by itself (Settings).
- **New products are added on the website.** A product in Admin → Products with a RotaHub package
  (the `rotahub` object in `/api/products`, see [docs/website-admin.md](docs/website-admin.md))
  is installable in RotaHub straight away: no RotaHub update needed.
- **Installs from signed releases** too. Each product's newest release is described by `rotahub.jws`,
  signed with the Rotaidem release key (the same key as HeavyPro's `update.jws`). RotaHub checks the
  signature and the package's SHA-256 before anything is unpacked. Products without a RotaHub release
  fall back to the download the website offers (their own installer).
- **Community folder links.** Packages are unpacked into a library folder (Settings) and appear in the
  simulator's Community folder through NTFS junctions: no administrator rights, any local drive,
  and an update just moves the link. RotaHub only replaces links it made, links into its library, or
  folders whose `manifest.json` names Rotaidem. It never touches the Community folder while the sim runs.
- **Installs itself.** The downloaded `RotaHub.exe` copies itself to `%LOCALAPPDATA%\Rotaidem\RotaHub\app`,
  adds Start menu and desktop shortcuts and an Apps list entry with an uninstaller, and updates itself
  from its own signed releases.

## Layout

| Path | What |
|---|---|
| `src/RotaHub.Core` | Everything but the UI: website API, signed releases, downloads, junctions, installer, licences. |
| `src/RotaHub` | The WPF app (.NET Framework 4.8, one exe). Compiles the Core and Licensing sources in. |
| `src/Rotaidem.Licensing` | Copied from `heavypro/src/Rotaidem.Licensing`. |
| `tests/RotaHub.Tests` | Signature checks, install/update/repair/uninstall against a temp Community folder with real junctions. |
| `packaging/New-HubManifest.ps1` | Signs a release manifest (used by this repo's CI and by each product's CI). |
| `packaging/licence-public-key.json` | rotaidem.com's licence key (`/api/licenses/jwks.json`). CI fails if it changes. |
| `packaging/release-public-key.json` | The Rotaidem release key (public half of `UPDATE_SIGNING_KEY`). |

## A product release for RotaHub

A package zip holds `app\…` (copied to the app folder) and `community\{folder}\…` for each Community
package. Sign it with:

```powershell
packaging/New-HubManifest.ps1 -Product rotaidem-heavypro -Name HeavyPro -Version 0.5.1 `
  -Package HeavyPro-rotahub.zip -Url https://github.com/…/HeavyPro-rotahub.zip -Out rotahub.jws `
  -AppDir '%LOCALAPPDATA%\HeavyPro' -AppExe HeavyPro.exe -Shortcut HeavyPro -Processes HeavyPro `
  -Community rotaidem-heavypro-panel:msfs2024
```

and publish both files on the release. RotaHub reads `…/releases/latest/download/rotahub.jws`
(the URL is per product in `HubCatalog.cs`).

## Develop

```powershell
dotnet test tests/RotaHub.Tests/RotaHub.Tests.csproj
dotnet build src/RotaHub/RotaHub.csproj
```

Debug builds run where they are (no self-install) and can use a local copy of the website:
`$env:ROTAHUB_SERVER = "http://127.0.0.1:8765"`. Release builds always use https://rotaidem.com.

Fonts: IBM Plex Sans (SIL Open Font License, `src/RotaHub/Assets/Fonts/OFL.txt`), as on rotaidem.com.
