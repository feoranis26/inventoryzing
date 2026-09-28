#!/usr/bin/env bash
# Ubuntu host prerequisites for the native or Docker USB printer agent.
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: sudo bash scripts/install-printer-usb.sh [--user USER] [--dry-run]

Installs libusb and USB diagnostic tools, creates the inventoryzing service
account/group, and installs device permissions for Brother QL-820NWB only.
--user USER   Also grant an existing login/service user access (log in again).
--dry-run     Show changes without installing packages or modifying the host.

Run on the Ubuntu host, not inside the printer container. This prepares USB;
it does not provision tokens, install .NET, start an agent, or print a label.
EOF
}

die() { printf 'Error: %s\n' "$*" >&2; exit 1; }
extra_user=''
dry_run=false
while (($#)); do
    case "$1" in
        --user)
            (($# >= 2)) && [[ -n "$2" && "$2" != -* ]] || die '--user requires an existing username.'
            extra_user=$2; shift 2 ;;
        --dry-run) dry_run=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *) die "Unknown option: $1 (see --help)." ;;
    esac
done

[[ $(uname -s) == Linux && -r /etc/os-release ]] || die 'Run this script on Ubuntu.'
# shellcheck disable=SC1091
. /etc/os-release
[[ ${ID:-} == ubuntu ]] || die 'This script supports Ubuntu; use your distribution package manager elsewhere.'
if ! "$dry_run"; then
    ((EUID == 0)) || die 'Run with sudo, or use --dry-run.'
    command -v udevadm >/dev/null && [[ -S /run/udev/control ]] ||
        die 'A running host udev service is required. Run on the Ubuntu host, not inside Docker.'
fi
if [[ -n "$extra_user" ]]; then
    getent passwd "$extra_user" >/dev/null || die "User does not exist: $extra_user"
fi

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
rule_source="$script_dir/../deploy/udev/70-inventoryzing-printer.rules"
rule_target=/etc/udev/rules.d/70-inventoryzing-printer.rules
[[ -f "$rule_source" ]] || die "Missing rule file: $rule_source (run from a complete checkout)."
[[ ! -L "$rule_target" ]] || die "Refusing to replace a symbolic link: $rule_target"

run() {
    if "$dry_run"; then printf 'Would run:'; printf ' %q' "$@"; printf '\n'
    else "$@"; fi
}

run apt-get update
run apt-get install -y --no-install-recommends libusb-1.0-0 usbutils
if ! getent group inventoryzing >/dev/null; then run groupadd --system inventoryzing; fi
if ! getent passwd inventoryzing >/dev/null; then
    run useradd --system --gid inventoryzing --home-dir /var/lib/inventoryzing \
        --no-create-home --shell /usr/sbin/nologin inventoryzing
else
    run usermod -a -G inventoryzing inventoryzing
fi
if [[ -n "$extra_user" && "$extra_user" != inventoryzing ]]; then
    run usermod -a -G inventoryzing "$extra_user"
fi

if "$dry_run"; then
    printf 'Would install %s (root:root, mode 0644), backing up any different existing rule.\n' "$rule_target"
else
    staging=$(mktemp)
    trap 'rm -f -- "$staging"' EXIT
    # Also tolerate a Windows checkout whose rule file has CRLF line endings.
    tr -d '\r' < "$rule_source" > "$staging"
    if [[ -e "$rule_target" ]] && ! cmp -s -- "$staging" "$rule_target"; then
        backup="${rule_target}.backup.$(date -u +%Y%m%dT%H%M%SZ).$$"
        cp -p -- "$rule_target" "$backup"
        printf 'Previous rule saved to %s\n' "$backup"
    fi
    install -D -o root -g root -m 0644 -- "$staging" "$rule_target"
fi
run udevadm control --reload-rules
# Target only the printer, never trigger every device on the system.
run udevadm trigger --action=change --subsystem-match=usb --attr-match=idVendor=04f9 --attr-match=idProduct=209d
run udevadm settle --timeout=10

if "$dry_run"; then
    printf 'Dry run complete; nothing changed.\n'
    exit 0
fi

gid=$(getent group inventoryzing | cut -d: -f3)
printf '\nUSB prerequisites installed. Docker Compose setting:\nIZ_PRINTER_USB_GID=%s\n' "$gid"
found=0
for device in /sys/bus/usb/devices/*; do
    [[ -r "$device/idVendor" && -r "$device/idProduct" ]] || continue
    [[ $(<"$device/idVendor") == 04f9 && $(<"$device/idProduct") == 209d ]] || continue
    found=$((found + 1))
    if [[ -r "$device/serial" ]]; then
        printf 'Connected QL-820NWB serial (use for IZ_PRINTER_USB_SERIAL): %s\n' "$(<"$device/serial")"
    else
        printf 'QL-820NWB detected, but no USB serial descriptor is available.\n'
    fi
done
if ((found == 0)); then
    printf 'Printer not detected. Power it on and connect its USB device port to this server.\n'
elif ((found > 1)); then
    printf 'Multiple printers detected; choose the serial of the intended printer.\n'
fi
if [[ -n "$extra_user" ]]; then printf 'Log out and in again for %s to receive group membership.\n' "$extra_user"; fi
printf '%s\n' \
    'Reconnect the printer if permissions have not refreshed. Restart an existing native agent to refresh its groups.' \
    'Disable any CUPS queue for this printer before direct USB use; other printer queues can remain active.' \
    'Continue with docs/operations/usb-raster-printing.md for agent credentials and startup.'
