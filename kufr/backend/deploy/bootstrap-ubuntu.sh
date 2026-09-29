#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 || $# -ne 1 || ! $1 =~ ^[a-z_][a-z0-9_-]*$ ]]; then
  echo 'Usage: sudo bash bootstrap-ubuntu.sh RUNNER_USER' >&2
  exit 2
fi

runner_user=$1
id "$runner_user" >/dev/null
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)

if ! id kufr-backend >/dev/null 2>&1; then
  useradd --system --home-dir /var/lib/kufr-backend --shell /usr/sbin/nologin kufr-backend
fi

install -d -o root -g root -m 0755 /opt/kufr-backend
install -d -o "$runner_user" -g "$runner_user" -m 0755 /opt/kufr-backend/releases
install -d -o kufr-backend -g kufr-backend -m 0700 /var/lib/kufr-backend
install -d -o root -g kufr-backend -m 0750 /etc/kufr-backend
install -o root -g root -m 0755 "$script_dir/deploy-helper.py" /usr/local/sbin/kufr-backend-deploy
install -o root -g root -m 0644 "$script_dir/kufr-backend.service" /etc/systemd/system/kufr-backend.service

sudoers_file=$(mktemp)
trap 'rm -f -- "$sudoers_file"' EXIT
printf '%s ALL=(root) NOPASSWD: /usr/local/sbin/kufr-backend-deploy activate *\n' "$runner_user" > "$sudoers_file"
visudo -cf "$sudoers_file" >/dev/null
install -o root -g root -m 0440 "$sudoers_file" /etc/sudoers.d/kufr-backend-deploy
systemctl daemon-reload
systemctl enable kufr-backend.service
echo 'Bootstrap complete; configure backend-production secrets and dispatch the workflow.'
