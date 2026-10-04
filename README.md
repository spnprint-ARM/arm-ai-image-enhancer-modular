# ARM AI Image Enhancer — Modular Edition (prototype)

This folder is an isolated prototype for the modular edition. It does not replace
or modify the tested V2 application, installer, or release files.

## Product direction

The modular installer installs the application shell. The Vulkan engine is an
optional post-install download. The first backend candidate for Windows is
Real-ESRGAN NCNN/Vulkan because its upstream project publishes portable binaries
for Intel, AMD, and NVIDIA GPUs. It still requires a compatible Vulkan-capable
GPU and driver. CPU fallback is not included in the current prototype.

Face recovery remains an optional add-on because it needs additional models and
dependencies. Do not advertise it as available until the add-on and its device
compatibility have been validated.

## Current contents

- `core/ModularApp.cs`: native Windows GUI with asynchronous hardware inventory,
  verified optional-module download, batch image enhancement, and scale selection.
- `prototype/device_probe.py`: read-only hardware and backend suitability
  inventory. It does not install software, contact the network, or modify V2.
- `installer/ModularSetup.cs`: separate Windows installer/uninstaller. It checks
  the payload SHA-256 and required files, shows activity before reading the
  archive and validates the QR image, extracts to a staging folder beside the install location, checks paths and
  files, then activates the application with rollback/cleanup. The installed
  icon is used by shortcuts.
- `installer/build_windows.ps1`: separate build flow, with a packaged-app
  self-check and installer-payload verification before publishing its output.
- `module-catalog.json`: initial catalog schema with optional packages disabled.
  No unverified package URLs or hashes are shipped.

Run the read-only hardware inventory using Python 3.10 or later:

```powershell
python .\outputs\modular\prototype\device_probe.py
```

Build the separate Windows installer on Windows with the .NET Framework C#
compiler available:

```powershell
& .\outputs\modular\installer\build_windows.ps1
```

The versioned installer output is `outputs\modular\release\setup-modular-V1.11.exe`;
build and staging files stay under `outputs\modular\installer\build`.

## Publishing a setup from GitHub

The GitHub Actions workflow `.github/workflows/release.yml` builds and attaches a
versioned setup when a published release has a tag such as `V1.11` or `v1.11`.
The workflow can also be run manually for an existing release by entering its
tag; this is useful to retry a failed asset build. Local setup files and build
staging folders are ignored by Git.

## Creating a downloadable module package

Do not enable a package in the app catalog until its archive is built, hosted at
the exact HTTPS URL, and the published file's SHA-256 has been checked. The
release helper creates a ZIP plus a catalog entry containing the byte size and
SHA-256:

```powershell
.\outputs\modular\tools\New-ModulePackage.ps1 `
  -Id upscale-ncnn-vulkan-windows-x64 `
  -Version 1.0.0 `
  -SourceDirectory C:\path\to\verified-module-files `
  -DownloadUrl https://your-host.example/modules/upscale-ncnn-vulkan-windows-x64-1.0.0-windows-x64.zip `
  -License "See included third-party license notices"
```

The generated JSON is a catalog-entry draft; it does not publish or sign files.
Use only official or otherwise authorized binaries and include all required
license notices in the source folder before packaging.

## Package manager progress

The Windows core checks the latest public GitHub Release for the official
Real-ESRGAN NCNN/Vulkan package. When the asset is present, it offers an install
button, shows download progress, verifies GitHub's SHA-256 asset digest, checks
archive paths, extracts to a staging folder, and activates it under the current
user's LocalAppData with rollback cleanup. The release workflow prepares the
Windows package from the upstream portable binary and includes its license.

The Vulkan package is optional and does not imply that a detected display adapter
can run inference. A compatible Vulkan driver/device must be present; CPU
inference is not part of this package. The enhancement screen accepts multiple
JPEG, PNG, or WebP images in one batch, offers stable 4x, uses the general x4plus
model, and saves separate files beside each source without overwriting existing
files. The output can keep the source format or be PNG, JPG, or TIFF. TIFF output
is converted locally after inference. Preview/compare tools, custom output sizing,
and face recovery are not implemented yet. Scales 2x and 3x are temporarily hidden
because they produced tiled images in the tested environment. The engine does not report a
percentage, so the app shows activity rather than a numeric progress estimate.

## Not release-ready

Face-recovery packages, CPU fallback, resume/cancel, and device validation on
representative Intel, AMD, and NVIDIA systems are not completed yet. The setup's embedded SHA-256
detects payload corruption, but is not a digital signature. The prototype
intentionally fails closed instead of downloading unchecked third-party binaries.
