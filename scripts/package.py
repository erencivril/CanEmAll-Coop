"""Build the release zips for Can 'Em All Co-op.

Run it in isolated mode so nothing from the current directory or the environment is imported:

    python -I scripts/package.py --plugin-dll path/to/CanEmAllCoop.dll --version 0.1.0 --out dist

It writes two zips into --out and prints their sha256:

  CanEmAll-Coop-<version>-full.zip         BepInEx 6.0.0-be.791 (IL2CPP, win-x64) + the plugin + install notes
                                           + LICENSE, THIRD_PARTY_NOTICES.md and licenses/.
                                           Extract it into the game folder.
  CanEmAll-Coop-<version>-plugin-only.zip  Only BepInEx/plugins/CanEmAllCoop/CanEmAllCoop.dll + install notes + LICENSE,
                                           for people who already have BepInEx be.791 installed.

Safety rules enforced by this script (it refuses to produce a zip that breaks them):
  * Only an allow-listed set of files is taken from the BepInEx zip (core/, dotnet/, doorstop files, empty plugin and
    patcher folders). Anything else in that zip is an error.
  * Game files, BepInEx/interop, unity-libs, config and cache folders are never packaged. The finished zips are
    re-opened and audited for this before the script reports success.
Standard library only.
"""

import argparse
import hashlib
import os
import re
import sys
import zipfile
from pathlib import Path
from typing import NoReturn

REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_BEPINEX_ZIP = REPO_ROOT / "vendor" / "bepinex" / "bepinex.zip"
BEPINEX_VERSION = "6.0.0-be.791"
BEPINEX_DOWNLOAD_HINT = (
    "BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.791.zip from https://builds.bepinex.dev/projects/bepinex_be "
    "(build 791), saved as vendor/bepinex/bepinex.zip"
)

PLUGIN_ARCNAME = "BepInEx/plugins/CanEmAllCoop/CanEmAllCoop.dll"
INSTALL_NOTES = "README-INSTALL.txt"
ZIP_TIME = (1980, 1, 1, 0, 0, 0)  # fixed timestamp: the same inputs give byte-identical zips

# What may be copied out of the BepInEx zip.
ALLOWED_FILES = {"winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt"}
ALLOWED_PREFIXES = ("BepInEx/core/", "dotnet/")
ALLOWED_EMPTY_DIRS = {"BepInEx/", "BepInEx/plugins/", "BepInEx/patchers/", "BepInEx/core/", "dotnet/"}

# Must never appear in any output zip (checked case-insensitively on every path segment / the whole path).
FORBIDDEN_SEGMENTS = {"interop", "unity-libs", "config", "cache", "can 'em all_data", "d3d12", "__pycache__"}
FORBIDDEN_SUBSTRINGS = ("gameassembly", "unityplayer", "can 'em all.exe", "save.sav", "settings.sav", "logoutput", "errorlog")

FULL_REQUIRED = [
    PLUGIN_ARCNAME, INSTALL_NOTES, "LICENSE", "THIRD_PARTY_NOTICES.md", "licenses/LGPL-2.1.txt",
    "winhttp.dll", "doorstop_config.ini", "dotnet/coreclr.dll",
    "BepInEx/core/BepInEx.Core.dll", "BepInEx/core/BepInEx.Unity.IL2CPP.dll",
]

INSTALL_FULL = """\
Can 'Em All Co-op {version} - full package (BepInEx + mod)
=========================================================

Unofficial fan-made co-op mod. Not affiliated with or endorsed by the developer of the game.
You need your own copy of Can 'Em All on Steam. This package contains no game files.

Install
-------
1. Close the game.
2. Open the game folder: in Steam, right click Can 'Em All > Manage > Browse local files.
   This is the folder that contains "Can 'Em All.exe".
3. Extract the WHOLE zip into that folder. Say yes if Windows asks to merge folders or overwrite files.
   Afterwards "winhttp.dll" must sit next to "Can 'Em All.exe", and the mod must be at
   BepInEx\\plugins\\CanEmAllCoop\\CanEmAllCoop.dll
4. Start the game from Steam.
   The FIRST launch takes about 1-3 minutes: BepInEx builds its interop assemblies from the game on first run.
   The game may show a black screen or no window for a while. Do not close it.
5. In the main menu press F7 to open the co-op menu.

Every player needs the game and the SAME mod version. This is version {version}.

More: https://github.com/erencivril/CanEmAll-Coop

Uninstall
---------
Delete these from the game folder: winhttp.dll, doorstop_config.ini, .doorstop_version, changelog.txt,
and the BepInEx and dotnet folders. Or let Steam do it: right click the game > Properties >
Installed Files > Verify integrity of game files.

The files LICENSE, THIRD_PARTY_NOTICES.md and the licenses folder in this zip are the license texts of the mod and
of the bundled BepInEx components. You may move them out of the game folder, but keep them with any copy of this zip
that you pass on.
"""

INSTALL_PLUGIN_ONLY = """\
Can 'Em All Co-op {version} - plugin only
=========================================

Unofficial fan-made co-op mod. Not affiliated with or endorsed by the developer of the game.
You need your own copy of Can 'Em All on Steam. This package contains no game files.

This zip is for people who ALREADY have BepInEx {bepinex} (IL2CPP, win-x64) installed in the game and have
started the game once with it. If you do not, download the "full" zip instead.

Install
-------
1. Close the game.
2. Open the game folder: in Steam, right click Can 'Em All > Manage > Browse local files.
3. Extract this zip into that folder. The result must be
   BepInEx\\plugins\\CanEmAllCoop\\CanEmAllCoop.dll
   (an older CanEmAllCoop.dll there is replaced).
4. Start the game and press F7 in the main menu.

Every player needs the SAME mod version. This is version {version}.

More: https://github.com/erencivril/CanEmAll-Coop
"""


def fail(message: str) -> NoReturn:
    sys.exit("error: " + message)


def sha256_of(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def check_name(name):
    """Reject anything that could escape the game folder or that must never ship."""
    if name.startswith("/") or "\\" in name or ".." in name.split("/") or re.match(r"^[A-Za-z]:", name):
        fail(f"unsafe path in package: {name!r}")
    lowered = name.lower()
    for segment in lowered.split("/"):
        if segment in FORBIDDEN_SEGMENTS:
            fail(f"forbidden path in package: {name!r}")
    for text in FORBIDDEN_SUBSTRINGS:
        if text in lowered:
            fail(f"forbidden path in package: {name!r}")


def load_bepinex_entries(zip_path):
    """Return a list of (arcname, bytes or None for a directory), taken only from the allow-list."""
    if not zip_path.is_file():
        fail(f"BepInEx zip not found: {zip_path}\n       Expected {BEPINEX_DOWNLOAD_HINT}\n       or pass --bepinex-zip.")
    entries = []
    rejected = []
    with zipfile.ZipFile(zip_path) as zf:
        bad = zf.testzip()
        if bad:
            fail(f"BepInEx zip is corrupt (first bad member: {bad})")
        for info in zf.infolist():
            name = info.filename
            if name.endswith("/"):
                if name in ALLOWED_EMPTY_DIRS:
                    entries.append((name, None))
                elif not name.startswith(ALLOWED_PREFIXES):
                    rejected.append(name)
                continue
            if name in ALLOWED_FILES or name.startswith(ALLOWED_PREFIXES):
                check_name(name)
                entries.append((name, zf.read(info)))
            else:
                rejected.append(name)
        names = {n for n, _ in entries}
        core = dict(entries).get("BepInEx/core/BepInEx.Core.dll")
    if rejected:
        fail("the BepInEx zip contains files outside the allow-list (refusing to repackage):\n  " + "\n  ".join(rejected[:20]))
    for required in ("winhttp.dll", "doorstop_config.ini", "dotnet/coreclr.dll", "BepInEx/core/BepInEx.Core.dll",
                     "BepInEx/core/BepInEx.Unity.IL2CPP.dll"):
        if required not in names:
            fail(f"{zip_path} does not look like BepInEx IL2CPP win-x64: missing {required}")
    if core is None:
        fail(f"{zip_path} does not look like BepInEx IL2CPP win-x64: BepInEx/core/BepInEx.Core.dll is not a file")
    needle = BEPINEX_VERSION.encode("ascii")
    if needle not in core and needle.decode().encode("utf-16-le") not in core:
        fail(f"{zip_path} is not BepInEx {BEPINEX_VERSION} (version string not found in BepInEx.Core.dll)")
    return entries


def load_licenses():
    entries = []
    for relative in ("LICENSE", "THIRD_PARTY_NOTICES.md"):
        path = REPO_ROOT / relative
        if not path.is_file():
            fail(f"missing {path}")
        entries.append((relative, path.read_bytes()))
    folder = REPO_ROOT / "licenses"
    files = sorted(p for p in folder.rglob("*") if p.is_file()) if folder.is_dir() else []
    if not any(p.name == "LGPL-2.1.txt" for p in files):
        fail(f"missing {folder / 'LGPL-2.1.txt'} (the full LGPL text must ship with BepInEx)")
    for path in files:
        entries.append(("licenses/" + path.relative_to(folder).as_posix(), path.read_bytes()))
    return entries


def write_zip(target, entries):
    seen = set()
    for name, _ in entries:
        if name in seen:
            fail(f"duplicate entry {name!r}")
        seen.add(name)
        check_name(name)
    tmp = target.with_name(target.name + ".tmp")
    with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        for name, data in sorted(entries, key=lambda item: item[0]):
            info = zipfile.ZipInfo(name, ZIP_TIME)
            if data is None:
                info.external_attr = (0o040755 << 16) | 0x10
                zf.writestr(info, b"")
            else:
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o100644 << 16
                zf.writestr(info, data, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
    os.replace(tmp, target)


def audit(path, kind):
    """Re-open a finished zip and check the rules again. Returns the sorted member names."""
    with zipfile.ZipFile(path) as zf:
        if zf.testzip():
            fail(f"{path.name}: corrupt")
        names = zf.namelist()
    for name in names:
        check_name(name)
    files = {n for n in names if not n.endswith("/")}
    if kind == "plugin-only":
        expected = {PLUGIN_ARCNAME, INSTALL_NOTES, "LICENSE"}
        if files != expected:
            fail(f"{path.name}: unexpected content {sorted(files ^ expected)}")
    else:
        missing = [n for n in FULL_REQUIRED if n not in files]
        if missing:
            fail(f"{path.name}: missing {missing}")
        stray_plugins = [n for n in files if n.startswith("BepInEx/plugins/") and n != PLUGIN_ARCNAME]
        if stray_plugins:
            fail(f"{path.name}: unexpected plugin files {stray_plugins}")
    return sorted(names)


def main():
    if not sys.flags.isolated:
        print("note: run this script as 'python -I scripts/package.py ...' (isolated mode)", file=sys.stderr)
    parser = argparse.ArgumentParser(description="Build the Can 'Em All Co-op release zips.")
    parser.add_argument("--plugin-dll", required=True, type=Path, help="built CanEmAllCoop.dll")
    parser.add_argument("--bepinex-zip", type=Path, default=DEFAULT_BEPINEX_ZIP,
                        help=f"BepInEx {BEPINEX_VERSION} IL2CPP win-x64 zip (default: vendor/bepinex/bepinex.zip in the repo)")
    parser.add_argument("--version", required=True, help="release version, e.g. 0.1.0 or 0.2.0-rc1")
    parser.add_argument("--out", required=True, type=Path, help="output directory (created if missing)")
    args = parser.parse_args()

    if not re.fullmatch(r"\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?", args.version):
        fail(f"--version must look like 1.2.3 or 1.2.3-rc1, got {args.version!r}")
    if not args.plugin_dll.is_file():
        fail(f"plugin dll not found: {args.plugin_dll}")
    dll = args.plugin_dll.read_bytes()
    if args.plugin_dll.suffix.lower() != ".dll" or dll[:2] != b"MZ" or len(dll) < 4096:
        fail(f"{args.plugin_dll} does not look like a built .NET assembly")

    install_full = INSTALL_FULL.format(version=args.version).replace("\n", "\r\n").encode("utf-8")
    install_plugin = INSTALL_PLUGIN_ONLY.format(version=args.version, bepinex=BEPINEX_VERSION).replace("\n", "\r\n").encode("utf-8")
    mod_license = (REPO_ROOT / "LICENSE")
    if not mod_license.is_file():
        fail(f"missing {mod_license}")

    full_entries = load_bepinex_entries(args.bepinex_zip.resolve())
    full_entries.append(("BepInEx/plugins/CanEmAllCoop/", None))
    full_entries.append((PLUGIN_ARCNAME, dll))
    full_entries.append((INSTALL_NOTES, install_full))
    full_entries.extend(load_licenses())

    plugin_entries = [
        ("BepInEx/plugins/CanEmAllCoop/", None),
        (PLUGIN_ARCNAME, dll),
        (INSTALL_NOTES, install_plugin),
        ("LICENSE", mod_license.read_bytes()),
    ]

    args.out.mkdir(parents=True, exist_ok=True)
    outputs = [
        (args.out / f"CanEmAll-Coop-{args.version}-full.zip", full_entries, "full"),
        (args.out / f"CanEmAll-Coop-{args.version}-plugin-only.zip", plugin_entries, "plugin-only"),
    ]
    for target, entries, kind in outputs:
        write_zip(target, entries)
        names = audit(target, kind)
        print(f"{target.name}: {len(names)} entries, {target.stat().st_size} bytes, audit ok")
    print()
    for target, _, _ in outputs:
        print(f"{sha256_of(target)}  {target.name}")


if __name__ == "__main__":
    main()
