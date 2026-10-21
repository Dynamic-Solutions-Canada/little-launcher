"""Run the native suite in the signed-in Windows session without an activating console."""

import argparse
import base64
import os
from pathlib import Path
import shutil
import subprocess


DEFAULT_CASES = (
    "child-reorder",
    "child-between-groups",
    "child-to-column",
    "item-to-empty-group",
    "group-cross-column",
    "group-over-child",
    "group-same-column",
    "cancel",
)

CASES = DEFAULT_CASES + (
    "group-pencil", "focus-return-done", "focus-body-done", "preview-target-change",
    "preview-target-cancel", "toolbar-settings", "item-into-group-end", "item-after-group",
    "child-after-own-group", "item-before-group", "item-into-group-header", "group-boundary-cancel", "group-end-leave",
)


def powershell_literal(value: str) -> str:
    """Quote a literal argument, including paths with spaces or apostrophes, for PowerShell."""
    return "'" + value.replace("'", "''") + "'"


def build_command(args: argparse.Namespace, suite: Path) -> str:
    """Build a fixed suite invocation; arrays remain typed PowerShell arrays across the process boundary."""
    command = [
        "&",
        powershell_literal(str(suite)),
        "-ExpectedComputerName",
        powershell_literal(args.expected_computer_name),
        "-AllowDesktopInteraction",
        "-SettingsPath",
        powershell_literal(str(args.settings_path)),
        "-OutputDirectory",
        powershell_literal(str(args.output_directory)),
        "-HostMode",
        powershell_literal(args.host_mode),
        "-Theme",
        str(args.theme),
        "-Modes @(" + ",".join(str(mode) for mode in args.modes) + ")",
        "-Cases @(" + ",".join(powershell_literal(case) for case in args.cases) + ")",
    ]
    return " ".join(command)


def main() -> int:
    """Validate the explicit desktop opt-in, then log the consoleless suite's exit status."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--expected-computer-name", required=True)
    parser.add_argument("--allow-desktop-interaction", action="store_true")
    parser.add_argument("--settings-path", required=True, type=Path)
    parser.add_argument("--output-directory", required=True, type=Path)
    parser.add_argument("--host-mode", choices=("regular", "native"), default="regular")
    parser.add_argument("--theme", type=int, choices=(1, 2), default=2)
    parser.add_argument("--modes", nargs="+", type=int, choices=(0, 1, 2), default=[1, 0, 2])
    parser.add_argument("--cases", nargs="+", choices=CASES, default=list(DEFAULT_CASES))
    parser.add_argument("--powershell", type=Path, help="Existing PowerShell 7 executable, if absent from PATH")
    args = parser.parse_args()

    if not args.allow_desktop_interaction:
        parser.error("--allow-desktop-interaction is required for this dedicated-desktop test")
    if os.name != "nt":
        parser.error("the native suite must run in the signed-in Windows desktop session")
    if os.environ.get("COMPUTERNAME", "").casefold() != args.expected_computer_name.casefold():
        parser.error("the current computer does not match --expected-computer-name")

    powershell = str(args.powershell) if args.powershell else shutil.which("pwsh.exe")
    if not powershell:
        parser.error("PowerShell 7 is required; supply its existing path with --powershell")

    suite = Path(__file__).resolve().with_name("native-drag-suite.ps1")
    # EncodedCommand transports literal paths and typed arrays. It does not provide secrecy.
    encoded = base64.b64encode(build_command(args, suite).encode("utf-16-le")).decode("ascii")
    args.output_directory.parent.mkdir(parents=True, exist_ok=True)
    host_log = args.output_directory.with_name(args.output_directory.name + ".host.log")
    with host_log.open("xb") as log:
        result = subprocess.run(
            [powershell, "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
            creationflags=subprocess.CREATE_NO_WINDOW,
            stdout=log,
            stderr=subprocess.STDOUT,
            check=False,
        )
    return result.returncode


if __name__ == "__main__":
    raise SystemExit(main())
