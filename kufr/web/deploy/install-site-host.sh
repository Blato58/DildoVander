#!/usr/bin/env bash
set -euo pipefail
if [[ ${EUID} -ne 0 || $# -ne 1 ]]; then
  echo 'Usage: sudo bash install-site-host.sh RUNNER_USER' >&2
  exit 2
fi
runner_user=$1
id -u "$runner_user" >/dev/null
runner_group=$(id -gn "$runner_user")
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
site=/etc/nginx/sites-available/kufr-ip
snippet=/etc/nginx/snippets/dildovander-site.conf
test -f "$site"
grep -q 'Managed by kufr IP HTTPS bootstrap' "$site"
test "$(readlink -f /etc/nginx/sites-enabled/kufr-ip)" = "$site"
systemctl is-active --quiet nginx.service
systemctl is-active --quiet kufr-backend.service
curl --fail --silent --show-error --max-time 10 -o /dev/null http://127.0.0.1:5080/healthz
for target in /var/www/dildovander /var/www/dildovander/releases "$site" "$snippet"; do
  if [[ -L $target ]]; then echo "Refusing symlink: $target" >&2; exit 1; fi
done
backup=$(mktemp -d /var/backups/dildovander-site.XXXXXXXX)
chmod 0700 "$backup"
cp -- "$site" "$backup/nginx-site.conf"
if [[ -e $snippet ]]; then cp -- "$snippet" "$backup/snippet.conf"; fi
for legacy in kufr-mechanika-prototyp.zip viewer.html; do
  if [[ -f /var/www/kufr-ios/$legacy && ! -L /var/www/kufr-ios/$legacy ]]; then
    cp -- "/var/www/kufr-ios/$legacy" "$backup/$legacy"
  fi
done
install -d -o "$runner_user" -g "$runner_group" -m 0755 /var/www/dildovander /var/www/dildovander/releases
install -m 0644 "$script_dir/site-locations.conf" "$snippet"
python3 - "$site" <<'PY'
from pathlib import Path
import sys
path = Path(sys.argv[1])
text = path.read_text()
include = '    include /etc/nginx/snippets/dildovander-site.con[f];'
legacy_include = '    include /etc/nginx/snippets/dildovander-site.conf;'
text = text.replace(legacy_include, include)
if include not in text:
    anchor = '    client_max_body_size 4m;'
    if text.count(anchor) != 1:
        raise SystemExit('Unexpected Nginx site; do not overwrite it')
    text = text.replace(anchor, anchor + '\n\n' + include)
path.write_text(text)
PY
if ! nginx -t || ! systemctl reload nginx.service || ! curl --fail --silent --show-error --max-time 10 -o /dev/null http://127.0.0.1:5080/healthz; then
  cp -- "$backup/nginx-site.conf" "$site"
  if [[ -f $backup/snippet.conf ]]; then cp -- "$backup/snippet.conf" "$snippet"; else rm -f -- "$snippet"; fi
  nginx -t
  systemctl reload nginx.service
  echo 'Site installation failed; previous configuration restored' >&2
  exit 1
fi
echo "Workshop routes installed; private backup=$backup; publication will populate current"
