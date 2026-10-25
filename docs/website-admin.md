# Adding products to RotaHub from the website

RotaHub shows every product from Admin → Products on rotaidem.com (name, pictures, tagline,
description, features, version history; drafts are hidden). To make a product **installable** in
RotaHub, the website adds a `rotahub` object to that product in `GET /api/products`. Nothing in
RotaHub has to change for a new product.

## What `/api/products` returns per product

```json
{
  "id": "rotaidem-skyfeel",
  "name": "Rotaidem SkyFeel",
  "...": "the existing fields",
  "rotahub": {
    "version": "1.2.0",
    "package_url": "/files/SkyFeel-1.2.0.zip",
    "sha256": "64 hex characters of the zip",
    "size": 1048576,
    "published": "2026-10-25",
    "notes": "What changed in this version (shown under What's new).",
    "app": {
      "dir": "%LOCALAPPDATA%\\SkyFeel",
      "exe": "SkyFeel.exe",
      "shortcut": "SkyFeel",
      "processes": ["SkyFeel"]
    },
    "community": [
      { "folder": "rotaidem-skyfeel-panel", "sim": "msfs2024" }
    ],
    "licence": {
      "prefix": "SKYF",
      "store": "skyfeel",
      "products": ["rotaidem-skyfeel"]
    }
  }
}
```

Leave `rotahub` out (or `null`) for a product that is not installable through RotaHub yet. RotaHub
then offers the product's normal download (`download_url`), if it has one.

| Field | Required | Meaning |
|---|---|---|
| `version` | yes | `1.2.0`. RotaHub offers an update when this is newer than what is installed. |
| `package_url` | yes* | The package zip: a site path (`/files/…`) or `https://rotaidem.com/…`, or a GitHub release of `tornqvisteliaz-cyber`. Anything else is refused. |
| `has_package` | * | For licence-only products (`download_gate: "licence"`): send `package_url: ""` and `has_package: true` to people without a licence, like `download_url` today. RotaHub then asks `POST /api/download/<id>` with body `{"kind": "rotahub"}` and expects `{"url": "<package url>"}`, checked against the licence like the normal download. |
| `sha256` | yes | SHA-256 of the zip, lowercase hex. RotaHub refuses a download that does not match. **Best computed by the server when the zip is uploaded**, not typed in. |
| `size` | no | Bytes, for the progress bar. |
| `published`, `notes` | no | Shown on the product page. |
| `app.dir` | no | Where the program goes. Must start with `%LOCALAPPDATA%\`. Empty: it runs from RotaHub's library folder. |
| `app.exe` | no | The program inside `app\` that **Launch** starts. Empty: no Launch button (e.g. a pure in-sim add-on). |
| `app.shortcut` | no | Start menu (and desktop) shortcut name. Empty: no shortcut. |
| `app.processes` | no | Process names RotaHub closes (after asking) before updating. |
| `community` | no | Packages linked into the sim's Community folder. `sim` is `msfs2024` or `msfs2020`. |
| `licence` | no | Only for products that use keys. `prefix` is the key prefix (`SKYF-XXXX-…`), `store` the folder the program reads its key from (`%LOCALAPPDATA%\Rotaidem\<store>\license.json`), `products` the licence product ids (defaults to the product id). Without `licence` the product installs without a key. |

## The package zip

```
app\                         → copied to app.dir (the program and its files)
community\<folder>\…         → one per entry in "community", linked into the Community folder
```

`community\<folder>` is a normal MSFS package (with its `manifest.json` and `layout.json`).

## Suggested admin form (Admin → Products → edit → "RotaHub")

- **Package**: upload a zip (stored under `/files/`, SHA-256 and size computed on upload) or paste a URL.
- **Version** and **What's new**.
- **Program**: folder name under `%LOCALAPPDATA%`, exe, shortcut name, process names.
- **In-sim packages**: list of Community folder names, each with MSFS 2024 / MSFS 2020.
- **Licence**: key prefix and store folder (off for free products).
- A **Remove from RotaHub** button that clears the whole `rotahub` object.

Store it as one JSON column on `products` (e.g. `rotahub TEXT`) and return it parsed in
`/api/products`, in both `server.py` and the Cloudflare function.

## HeavyPro

HeavyPro also has signed GitHub releases (`rotahub.jws`). When both exist, RotaHub installs the newer
one, and the signed one when the versions are the same.
