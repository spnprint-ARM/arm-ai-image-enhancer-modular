# Modular Edition design notes

## Preserve V2

All modular work belongs under `outputs/modular/`. The V2 application, installer,
build output, and existing release artifact are treated as read-only during this
work so they remain available while V2 is tested on another computer.

## Proposed modules

| Module | Install timing | Role |
| --- | --- | --- |
| Core application | Initial setup | GUI, image I/O, settings, job queue, progress, update/module manager |
| CPU inference | Initial or first use | Always-available compatibility path; slower, but avoids GPU runtime assumptions |
| Vulkan inference | Optional post-install | Shared Intel/AMD/NVIDIA GPU path using the upstream NCNN/Vulkan executable and its model files |
| CUDA inference | Optional post-install | NVIDIA/PyTorch path only when exact driver, runtime, and package compatibility checks pass |
| ROCm inference | Optional post-install | AMD/PyTorch path only for a GPU/OS combination listed in AMD's current Windows support matrix |
| DirectML inference | Experimental optional module | DirectX 12 path; remains labeled experimental until the current PyTorch operator coverage and performance are validated |
| Face recovery | Optional post-install | Face detector/restorer and model files; separate from ordinary upscaling |
| AI model packs | On demand | Upscale weights by model and scale, independently removable/re-downloadable |

## Windows hardware policy

1. Inventory display adapters and Windows version. Never infer that a vendor name
   alone means an accelerator is usable.
2. Prefer checking the installed Vulkan device list and running a small inference
   probe over installing a vendor SDK. NCNN/Vulkan is the broadest candidate for
   old Intel/AMD/NVIDIA systems, but a compatible Vulkan driver is still required.
3. Keep CPU inference available as the baseline. If a device probe fails, explain
   that GPU acceleration is unavailable and allow CPU operation.
4. Offer CUDA only for supported NVIDIA hardware and package versions. PyTorch's
   normal Windows installation path exposes CUDA and CPU options.
5. Do not treat ROCm as a generic AMD option. AMD's Windows support is limited to
   specific Radeon/Ryzen products and OS/software combinations; query the official
   compatibility matrix before offering the module.
6. Treat DirectML as experimental for this application. Microsoft documents the
   Windows PyTorch adapter, but the complete Real-ESRGAN/GFPGAN operator stack
   must be validated against the exact `torch-directml` version first.
7. Report actual device/runtime from a successful inference probe, not only the
   display adapter name.

## Package catalog requirements

Before any package can be downloadable, the release catalog must include a pinned
version, official source URL, archive size, SHA-256, supported OS/device matrix,
license notice, package role, and install/uninstall actions. The client must
download to a temporary file, verify size and hash, extract to a staging folder,
probe the package, then atomically activate it. A failed install must leave the
previous working module intact and remove staging files.

The current catalog is deliberately unconfigured: the upstream GitHub release
does not provide a verified digest through the source inspection performed for
this prototype, so no auto-download is enabled yet. A project-controlled signed
catalog or a verified release pipeline should supply hashes.

## Delivery stages

1. Hardware probe, minimal GUI shell, and separate installer prototype
   (implemented in this working tree).
2. Authenticated online package catalog, package publishing and verification.
3. Verified module downloader, resume/cancel, integrity check, staging,
   rollback, and disk-space estimate.
4. Vulkan inference integration and CPU fallback, tested on representative Intel,
   AMD, and NVIDIA systems.
5. Separate CUDA/ROCm/DirectML experiments behind explicit compatibility gates.
6. Optional face-recovery pack and model management.

## Known limits

- Display adapter enumeration is not a Vulkan capability test.
- NCNN/Vulkan scale support and model selection differ from the Python engine;
  matching V2's 2x/4x/8x and face workflow needs separate product decisions.
- The old Intel HD 630 / Radeon 530 combination shown in the earlier diagnostic
  screenshots must be tested with its actual drivers before claiming GPU support.
- Downloading and running accelerator packages should remain optional; the app
  should work on CPU without first downloading multi-gigabyte frameworks.
