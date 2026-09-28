#!/usr/bin/env bash
# Run only in a disposable Ubuntu container as root, with the repo mounted /workspace.
# Package installation and udev are mocked; account/rule changes occur in the container.
set -euo pipefail
[[ -f /.dockerenv && $EUID == 0 ]] || { echo 'Requires a disposable root Docker container.' >&2; exit 1; }
[[ ! -e /run/udev/control && ! -e /etc/udev/rules.d/70-inventoryzing-printer.rules ]] || exit 1
! getent group inventoryzing >/dev/null || exit 1
installer=/workspace/scripts/install-printer-usb.sh
scratch=$(mktemp -d)
export INSTALLER_TEST_CALLS="$scratch/calls"
mkdir "$scratch/bin"
for command in apt-get udevadm; do
    cat > "$scratch/bin/$command" <<'EOF'
#!/usr/bin/env bash
printf '%s %s\n' "${0##*/}" "$*" >> "$INSTALLER_TEST_CALLS"
EOF
    chmod +x "$scratch/bin/$command"
done
export PATH="$scratch/bin:$PATH"

bash -n "$installer"
bash "$installer" --dry-run --user root > "$scratch/dry-run"
[[ ! -e "$INSTALLER_TEST_CALLS" ]]
! getent group inventoryzing >/dev/null
if bash "$installer" > "$scratch/no-udev" 2>&1; then exit 1; fi
grep -q 'running host udev service' "$scratch/no-udev"
[[ ! -e "$INSTALLER_TEST_CALLS" ]]

# A local socket fixture satisfies the host-service precheck; no devices are mounted.
python3 - <<'PY'
import os, socket
os.makedirs('/run/udev', exist_ok=True)
s = socket.socket(socket.AF_UNIX)
s.bind('/run/udev/control')
s.close()
PY
if bash "$installer" --user inventoryzing-no-such-user > "$scratch/no-user" 2>&1; then exit 1; fi
[[ ! -e "$INSTALLER_TEST_CALLS" ]]
bash "$installer" --user root > "$scratch/first"
rule=/etc/udev/rules.d/70-inventoryzing-printer.rules
[[ $(stat -c '%a:%U:%G' "$rule") == 644:root:root ]]
grep -q 'MODE="0660"' "$rule"
gid=$(getent group inventoryzing | cut -d: -f3)
id -G root | tr ' ' '\n' | grep -qx "$gid"
[[ $(getent passwd inventoryzing | cut -d: -f7) == /usr/sbin/nologin ]]
grep -q "IZ_PRINTER_USB_GID=$gid" "$scratch/first"
grep -qx 'apt-get install -y --no-install-recommends libusb-1.0-0 usbutils' "$INSTALLER_TEST_CALLS"
grep -qx 'udevadm trigger --action=change --subsystem-match=usb --attr-match=idVendor=04f9 --attr-match=idProduct=209d' "$INSTALLER_TEST_CALLS"

bash "$installer" > "$scratch/second"
[[ $(getent group inventoryzing | cut -d: -f3) == "$gid" ]]
! compgen -G "$rule.backup.*" >/dev/null
printf '# existing custom rule\n' > "$rule"
bash "$installer" > "$scratch/third"
backups=("$rule".backup.*)
[[ ${#backups[@]} == 1 ]]
grep -qx '# existing custom rule' "${backups[0]}"
grep -q 'idProduct}=="209d"' "$rule"
echo 'PASS: dry-run, host guard, invalid user, account and group setup, scoped rule, repeat install, backup.'
