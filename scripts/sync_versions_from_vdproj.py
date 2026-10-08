"""Synchronize application and MSI output versions from the setup project's ProductVersion."""

from __future__ import annotations

import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
VDPROJ_PATH = ROOT / "PersonalViewer.Setup" / "PersonalViewer.Setup.vdproj"
CSPROJ_PATH = ROOT / "PersonalViewer" / "PersonalViewer.csproj"


def replace_one(text: str, pattern: re.Pattern[str], replacement: str, label: str) -> tuple[str, bool]:
    updated, count = pattern.subn(replacement, text)
    if count != 1:
        raise ValueError(f"Expected exactly one {label}, found {count}.")
    return updated, updated != text


def read_product_version(vdproj_text: str) -> str:
    pattern = re.compile(
        r'(?m)^[ \t]*"ProductVersion"[ \t]*=[ \t]*"8:(?P<version>[^"]+)"[ \t]*\r?$'
    )
    matches = list(pattern.finditer(vdproj_text))
    if len(matches) != 1:
        raise ValueError(f"Expected exactly one ProductVersion in {VDPROJ_PATH}, found {len(matches)}.")

    version = matches[0].group("version")
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError(f"ProductVersion must have three numeric parts, for example 0.8.1: {version!r}")

    major, minor, build = (int(part) for part in version.split("."))
    if major > 255 or minor > 255 or build > 65535:
        raise ValueError(f"ProductVersion is outside Windows Installer's supported range: {version}")
    return version


def update_csproj(csproj_text: str, version: str) -> tuple[str, list[str]]:
    changed_fields: list[str] = []
    assembly_version = f"{version}.0"
    values = {
        "Version": version,
        "AssemblyVersion": assembly_version,
        "FileVersion": assembly_version,
        "InformationalVersion": version,
    }

    for field, value in values.items():
        pattern = re.compile(
            rf"(?m)^(?P<prefix>[ \t]*<{field}>)[^<]*(?P<suffix></{field}>[ \t]*\r?)$"
        )
        matches = list(pattern.finditer(csproj_text))
        if len(matches) != 1:
            raise ValueError(f"Expected exactly one <{field}> in {CSPROJ_PATH}, found {len(matches)}.")

        match = matches[0]
        if match.group(0) != f"{match.group('prefix')}{value}{match.group('suffix')}":
            csproj_text = pattern.sub(
                lambda current: f"{current.group('prefix')}{value}{current.group('suffix')}",
                csproj_text,
                count=1,
            )
            changed_fields.append(field)

    return csproj_text, changed_fields


def update_output_filenames(vdproj_text: str, version: str) -> tuple[str, int]:
    pattern = re.compile(
        r'(?m)^(?P<prefix>[ \t]*"OutputFilename"[ \t]*=[ \t]*"8:[^"\r\n]*?PersonalViewer-)'
        r'(?P<version>[^"\\/\r\n]+)(?P<suffix>\.msi"[ \t]*\r?)$'
    )
    matches = list(pattern.finditer(vdproj_text))
    if len(matches) != 2:
        raise ValueError(f"Expected Debug and Release OutputFilename entries in {VDPROJ_PATH}, found {len(matches)}.")

    updated, count = pattern.subn(
        lambda match: f"{match.group('prefix')}{version}{match.group('suffix')}",
        vdproj_text,
    )
    return updated, sum(match.group("version") != version for match in matches)


def write_if_changed(path: Path, original: bytes, updated_text: str) -> bool:
    updated = updated_text.encode("utf-8")
    if updated == original:
        return False
    path.write_bytes(updated)
    return True


def main() -> int:
    try:
        vdproj_original = VDPROJ_PATH.read_bytes()
        csproj_original = CSPROJ_PATH.read_bytes()
        vdproj_text = vdproj_original.decode("utf-8")
        csproj_text = csproj_original.decode("utf-8")

        version = read_product_version(vdproj_text)
        updated_csproj_text, changed_fields = update_csproj(csproj_text, version)
        updated_vdproj_text, changed_output_names = update_output_filenames(vdproj_text, version)

        csproj_changed = write_if_changed(CSPROJ_PATH, csproj_original, updated_csproj_text)
        vdproj_changed = write_if_changed(VDPROJ_PATH, vdproj_original, updated_vdproj_text)

        print(f"ProductVersion: {version}")
        print(f"Updated application version fields: {', '.join(changed_fields) if changed_fields else 'none'}")
        print(f"Updated MSI output filenames: {changed_output_names}")
        if csproj_changed or vdproj_changed:
            print("Version values were synchronized.")
        else:
            print("All version values were already synchronized.")
        return 0
    except (OSError, UnicodeError, ValueError) as error:
        print(f"Version synchronization failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
