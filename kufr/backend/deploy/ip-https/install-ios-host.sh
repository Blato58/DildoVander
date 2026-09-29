#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 || $# -ne 2 ]]; then
  echo 'Usage: sudo bash install-ios-host.sh PUBLIC_IPV4 RUNNER_USER' >&2
  exit 2
fi

public_ip=$1
runner_user=$2
python3 - "$public_ip" <<'PY'
import ipaddress
import sys

address = ipaddress.IPv4Address(sys.argv[1])
if not address.is_global:
    raise SystemExit('A public IPv4 address is required')
PY
id -u "$runner_user" >/dev/null
runner_group=$(id -gn "$runner_user")

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
site=/etc/nginx/sites-available/kufr-ip
test -f "$script_dir/kufr-ip-https.conf.in"
test -f "$script_dir/ios-placeholder.html"
test -f "$site"
grep -q 'Managed by kufr IP HTTPS bootstrap' "$site"
test -L /etc/nginx/sites-enabled/kufr-ip
test "$(readlink -f /etc/nginx/sites-enabled/kufr-ip)" = "$site"
test -f "/etc/letsencrypt/live/$public_ip/fullchain.pem"
test -f "/etc/letsencrypt/live/$public_ip/privkey.pem"
systemctl is-active --quiet nginx.service

if [[ -L /var/www/kufr-ios || -L /var/www/kufr-ios/index.html ]]; then
  echo 'Refusing an iOS site symlink' >&2
  exit 1
fi
install -d -o "$runner_user" -g "$runner_group" -m 0755 /var/www/kufr-ios
if [[ ! -e /var/www/kufr-ios/index.html ]]; then
  install -o "$runner_user" -g "$runner_group" -m 0644 \
    "$script_dir/ios-placeholder.html" /var/www/kufr-ios/index.html
fi

previous_site=$(mktemp)
cp -- "$site" "$previous_site"
sed "s/__PUBLIC_IP__/$public_ip/g" "$script_dir/kufr-ip-https.conf.in" > "$site"
if ! nginx -t || ! systemctl reload nginx.service ||
    ! curl --fail --silent --show-error --max-time 10 --connect-to \
      "$public_ip:443:127.0.0.1:443" -o /dev/null "https://$public_ip/ios/"; then
  cp -- "$previous_site" "$site"
  nginx -t
  systemctl reload nginx.service
  rm -f -- "$previous_site"
  echo 'iOS site installation failed; previous Nginx site restored' >&2
  exit 1
fi
rm -f -- "$previous_site"
echo "iOS install site ready at https://$public_ip/ios/"
