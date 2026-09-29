#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 || $# -ne 1 ]]; then
  echo 'Usage: sudo bash bootstrap-ip-https.sh PUBLIC_IPV4' >&2
  exit 2
fi

public_ip=$1
python3 - "$public_ip" <<'PY'
import ipaddress
import sys

try:
    address = ipaddress.IPv4Address(sys.argv[1])
except ipaddress.AddressValueError as error:
    raise SystemExit(f'Invalid public IPv4 address: {error}')
if not address.is_global:
    raise SystemExit('The HTTPS address must be a public IPv4 address')
PY

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
for source_file in kufr-ip-http.conf.in kufr-ip-https.conf.in kufr-cert-renew.service kufr-cert-renew.timer kufr-cert-reload.sh; do
  test -f "$script_dir/$source_file" || {
    echo "Missing bootstrap source: $source_file" >&2
    exit 1
  }
done

test -x /usr/bin/python3
test -x /usr/bin/ss
test -x /usr/bin/systemctl
test -x /usr/bin/apt-get
test -x /usr/bin/snap
test -x /usr/bin/openssl
test -x /usr/bin/curl
test -f /etc/kufr-backend/backend.env
systemctl is-active --quiet kufr-backend.service
curl --fail --silent --show-error --max-time 5 -o /dev/null http://127.0.0.1:5080/healthz

site_available=/etc/nginx/sites-available/kufr-ip
site_enabled=/etc/nginx/sites-enabled/kufr-ip
if [[ ! -e $site_available ]] && ss -H -lnt '( sport = :80 or sport = :443 )' | grep -q .; then
  echo 'Port 80 or 443 is already in use; inspect the existing service first' >&2
  exit 1
fi

export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y nginx
if ! snap list certbot >/dev/null 2>&1; then
  snap install --classic certbot
fi
certbot=/snap/bin/certbot
certbot_version=$($certbot --version | awk '{print $2}')
if ! dpkg --compare-versions "$certbot_version" ge 5.4; then
  echo "Certbot 5.4 or newer is needed for IP webroot certificates; found $certbot_version" >&2
  exit 1
fi

install -d -o root -g root -m 0755 /var/www/kufr-acme/.well-known/acme-challenge
if [[ -e $site_available ]] && ! grep -q 'Managed by kufr IP HTTPS bootstrap' "$site_available"; then
  echo 'Refusing to replace an unmanaged Nginx site' >&2
  exit 1
fi
for existing_site in /etc/nginx/sites-enabled/*; do
  [[ -e $existing_site || -L $existing_site ]] || continue
  if [[ $existing_site == /etc/nginx/sites-enabled/default ]] &&
      [[ -L $existing_site ]] &&
      [[ $(readlink -f "$existing_site") == /etc/nginx/sites-available/default ]]; then
    continue
  fi
  if [[ $existing_site != "$site_enabled" ]]; then
    echo "Refusing to replace another Nginx site: $existing_site" >&2
    exit 1
  fi
done

if [[ -L /etc/nginx/sites-enabled/default ]] &&
    [[ $(readlink -f /etc/nginx/sites-enabled/default) == /etc/nginx/sites-available/default ]]; then
  rm -- /etc/nginx/sites-enabled/default
fi
if [[ -L $site_enabled ]] &&
    [[ $(readlink -f "$site_enabled") != "$site_available" ]]; then
  echo 'Refusing to replace an unmanaged Nginx site link' >&2
  exit 1
fi
if [[ -e $site_enabled && ! -L $site_enabled ]]; then
  echo 'Refusing to replace an unmanaged Nginx site file' >&2
  exit 1
fi
if [[ ! -e $site_enabled && ! -L $site_enabled ]]; then
  ln -s "$site_available" "$site_enabled"
fi
if [[ ! -e $site_available ]]; then
  sed "s/__PUBLIC_IP__/$public_ip/g" \
    "$script_dir/kufr-ip-http.conf.in" > "$site_available"
fi
nginx -t
systemctl enable --now nginx.service
systemctl reload nginx.service

if command -v ufw >/dev/null 2>&1 && ufw status | grep -q '^Status: active'; then
  ufw allow 80/tcp
  ufw allow 443/tcp
fi

challenge_file=/var/www/kufr-acme/.well-known/acme-challenge/kufr-bootstrap-check
printf 'ok\n' > "$challenge_file"
trap 'rm -f -- "$challenge_file"' EXIT
if [[ $(curl --fail --silent --show-error --max-time 5 "http://127.0.0.1/.well-known/acme-challenge/kufr-bootstrap-check") != ok ]]; then
  echo 'Local ACME webroot challenge is not served by Nginx' >&2
  exit 1
fi
rm -f -- "$challenge_file"
trap - EXIT

certificate_dir="/etc/letsencrypt/live/$public_ip"
if [[ ! -f $certificate_dir/fullchain.pem || ! -f $certificate_dir/privkey.pem ]]; then
  "$certbot" certonly --non-interactive --agree-tos \
    --register-unsafely-without-email \
    --preferred-profile shortlived \
    --webroot --webroot-path /var/www/kufr-acme \
    --ip-address "$public_ip"
else
  "$certbot" renew --cert-name "$public_ip" --non-interactive --quiet
fi
openssl x509 -in "$certificate_dir/fullchain.pem" -noout -ext subjectAltName |
  grep -F "IP Address:$public_ip" >/dev/null
openssl x509 -in "$certificate_dir/fullchain.pem" -noout -checkend 86400 >/dev/null

prior_site=$(mktemp)
cp -- "$site_available" "$prior_site"
sed "s/__PUBLIC_IP__/$public_ip/g" "$script_dir/kufr-ip-https.conf.in" > "$site_available"
if ! nginx -t || ! systemctl reload nginx.service; then
  cp -- "$prior_site" "$site_available"
  nginx -t
  systemctl reload nginx.service
  rm -f -- "$prior_site"
  echo 'HTTPS Nginx configuration failed; previous site restored' >&2
  exit 1
fi
rm -f -- "$prior_site"
install -o root -g root -m 0755 "$script_dir/kufr-cert-reload.sh" \
  /etc/letsencrypt/renewal-hooks/deploy/kufr-cert-reload.sh
install -o root -g root -m 0644 "$script_dir/kufr-cert-renew.service" \
  /etc/systemd/system/kufr-cert-renew.service
install -o root -g root -m 0644 "$script_dir/kufr-cert-renew.timer" \
  /etc/systemd/system/kufr-cert-renew.timer
systemctl daemon-reload
systemctl enable --now kufr-cert-renew.timer
curl --fail --silent --show-error --max-time 10 --connect-to \
  "$public_ip:443:127.0.0.1:443" -o /dev/null "https://$public_ip/healthz"
"$certbot" renew --cert-name "$public_ip" --dry-run \
  --run-deploy-hooks --non-interactive
echo "IP HTTPS active at https://$public_ip/healthz; renewal timer enabled and dry-run passed"
