"""Read-only advisory device inventory for the Modular Edition prototype."""

from __future__ import annotations

import json
import platform
import shutil
import subprocess
import sys
try:
    import winreg
except ImportError:  # pragma: no cover - available only on Windows
    winreg = None
from dataclasses import asdict, dataclass


@dataclass(frozen=True)
class Adapter:
    name: str
    driver_version: str = ""
    driver_date: str = ""
    status: str = ""
    source: str = "WMI/CIM"


def _powershell_path() -> str | None:
    return shutil.which("pwsh") or shutil.which("powershell") or shutil.which("powershell.exe")


def display_adapters() -> tuple[list[Adapter], str | None]:
    if platform.system() != "Windows":
        return [], "Display adapter query is Windows-only."

    executable = _powershell_path()
    if not executable:
        return [], "PowerShell was not found; display adapters could not be queried."

    script = (
        "Get-CimInstance Win32_VideoController | "
        "Select-Object Name,DriverVersion,DriverDate,Status | ConvertTo-Json -Compress"
    )
    try:
        result = subprocess.run(
            [executable, "-NoProfile", "-NonInteractive", "-Command", script],
            capture_output=True,
            text=True,
            timeout=15,
            check=False,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        return [], f"Display adapter query failed: {exc}"

    if result.returncode != 0 or not result.stdout.strip():
        message = result.stderr.strip() or result.stdout.strip() or f"PowerShell exited with code {result.returncode}."
        # WMI/CIM can be blocked in managed environments. Registry data is a
        # read-only fallback and is labelled advisory because disconnected
        # adapters may also appear there.
        fallback = _registry_adapters()
        if fallback:
            return fallback, f"WMI/CIM unavailable; using registry inventory. {message}"
        return [], message or "Windows returned no display adapters."

    try:
        rows = json.loads(result.stdout)
    except json.JSONDecodeError as exc:
        return [], f"Could not parse adapter information: {exc}"
    if isinstance(rows, dict):
        rows = [rows]
    return [
        Adapter(
            name=str(row.get("Name") or "Unknown display adapter"),
            driver_version=str(row.get("DriverVersion") or ""),
            driver_date=str(row.get("DriverDate") or ""),
            status=str(row.get("Status") or ""),
            source="WMI/CIM",
        )
        for row in rows
    ], None


def _registry_adapters() -> list[Adapter]:
    if platform.system() != "Windows" or winreg is None:
        return []
    results: list[Adapter] = []
    path = r"SYSTEM\CurrentControlSet\Control\Video"
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, path) as root:
            for index in range(winreg.QueryInfoKey(root)[0]):
                try:
                    guid = winreg.EnumKey(root, index)
                    with winreg.OpenKey(root, guid) as guid_key:
                        for sub_index in range(winreg.QueryInfoKey(guid_key)[0]):
                            try:
                                subkey = winreg.EnumKey(guid_key, sub_index)
                                with winreg.OpenKey(guid_key, subkey) as adapter_key:
                                    name = str(winreg.QueryValueEx(adapter_key, "DriverDesc")[0])
                                    try:
                                        version = str(winreg.QueryValueEx(adapter_key, "DriverVersion")[0])
                                    except OSError:
                                        version = ""
                                    if name and not any(item.name.casefold() == name.casefold() for item in results):
                                        results.append(Adapter(name=name, driver_version=version, source="Windows registry (advisory)"))
                            except OSError:
                                continue
                except OSError:
                    continue
    except OSError:
        return []
    return results


def advisory_candidates(adapters: list[Adapter]) -> list[dict[str, str]]:
    names = " ".join(adapter.name.casefold() for adapter in adapters)
    candidates = [
        {
            "backend": "CPU",
            "status": "baseline",
            "note": "Available as the compatibility path; performance depends on CPU.",
        }
    ]
    if adapters:
        candidates.append(
            {
                "backend": "NCNN/Vulkan",
                "status": "candidate-unverified",
                "note": "May use a supported Intel, AMD, or NVIDIA adapter; requires a Vulkan runtime probe.",
            }
        )
    if "nvidia" in names or "geforce" in names or "quadro" in names:
        candidates.append(
            {
                "backend": "CUDA",
                "status": "candidate-unverified",
                "note": "NVIDIA adapter detected; CUDA/driver/PyTorch compatibility is not yet tested.",
            }
        )
    if "amd" in names or "radeon" in names:
        candidates.append(
            {
                "backend": "ROCm on Windows",
                "status": "restricted-candidate",
                "note": "Offer only when the exact GPU, Windows version, and AMD-supported package matrix match.",
            }
        )
    if "intel" in names:
        candidates.append(
            {
                "backend": "DirectML",
                "status": "experimental-candidate",
                "note": "DirectX 12 adapter detected; full model/operator support needs application-level validation.",
            }
        )
    return candidates


def collect_report() -> dict[str, object]:
    adapters, error = display_adapters()
    return {
        "os": platform.platform(),
        "python": sys.version.split()[0],
        "adapters": [asdict(adapter) for adapter in adapters],
        "query_error": error,
        "backend_candidates": advisory_candidates(adapters),
        "warning": "Inventory is advisory; it does not prove that a backend can run inference.",
    }


def main() -> int:
    report = collect_report()
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
