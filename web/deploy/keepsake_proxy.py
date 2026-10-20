#!/usr/bin/env python3
"""Render the DV10 exact guest route/method allowlist for an existing nginx server."""

import argparse
import json
from pathlib import Path
import re


GUID = r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
ROOT = "/guest/v1/keepsakes/" + GUID
ROUTES = [
    (ROOT + "/account-associations", {"POST"}),
    ("/guest/v1/chronicle", {"GET"}),
    ("/guest/v1/duels/" + GUID, {"GET"}),
    ("/guest/v1/duels/" + GUID + "/choices", {"POST"}),
    ("/guest/v1/evening", {"GET"}),
    ("/guest/v1/evenings/" + GUID + "/keepsakes/" + GUID + "/editions/" + GUID + "/assets/" + GUID, {"GET"}),
    ("/guest/v1/(?:bootstrap|claim|recover|share-claim)", {"POST"}),
    (ROOT, {"GET", "DELETE"}),
    (ROOT + "/assets/" + GUID, {"GET"}),
    (ROOT + "/history", {"GET"}),
    (ROOT + "/(?:recovery-links|share-links)", {"POST", "DELETE"}),
    (ROOT + "/(?:exports|portrait-consents|portrait-uploads|portraits|revoke-access|wizard-identity)", {"POST"}),
    (ROOT + "/(?:recovery-links|share-links)/" + GUID, {"DELETE"}),
    (ROOT + "/portrait", {"DELETE"}),
    (ROOT + "/(?:portraits|generation)/" + GUID + "/cancel", {"POST"}),
    ("/guest/v1/deletions/" + GUID, {"GET"}),
]

CONTRACT = json.loads(Path(__file__).with_name("backend-route-contract.json").read_text(encoding="utf-8"))


def route_pattern(template):
    parts = re.split(r"(\{[^}]+\})", template)
    variables = {"channel": "(?:stable|development)", "hash": "[0-9a-f]{64}",
                 "deviceId": "[A-Za-z0-9_-]{1,128}", "sourceId": "[A-Za-z0-9_.:-]{1,240}",
                 "scope": "(?:conversation|guest|evening)", "subject": "[A-Za-z0-9_.:-]{1,240}"}
    # Twin instance IDs follow TwinSnapshot.Id, rather than conversation GUIDs.
    if template.startswith("/v1/twin/"):
        variables["id"] = "[A-Za-z0-9_.-]{1,96}"
    if template == "/v1/admin/memory/scopes/{scope}/{subject}":
        variables["scope"] = "(?:conversation|guest|evening)"
        # Guest IDs are bounded ASCII names; conversation/evening UUIDs fit too.
        variables["subject"] = "(?![.]{1,2}$)[A-Za-z0-9_.-]{1,80}"
    return "".join(variables.get(part[1:-1].split(":")[0], GUID) if part.startswith("{") else re.escape(part) for part in parts)


def allowed_backend(method, path):
    return any(method == expected and re.fullmatch(route_pattern(template), path) for expected, template, source in CONTRACT["routes"])


def render_http_map():
    return "# Include once in nginx http scope (e.g. /etc/nginx/conf.d/keepsake-upgrade.conf).\nmap $http_upgrade $keepsake_connection_upgrade {\n    default upgrade;\n    '' close;\n}\n"


def render_compatibility(host):
    # Static routes remain source-owned; do not duplicate a separate site include.
    lines = [render(host, guest_only=False),
             "if ($host != " + host + ") { return 421; }",
             Path(__file__).with_name("site-locations.conf").read_text(encoding="utf-8")]
    grouped = {}
    for method, template, source in CONTRACT["routes"]:
        grouped.setdefault((template, source), set()).add(method)
    for (template, source), methods in grouped.items():
        streaming = template.endswith(("/realtime-turns", "/realtime-live", "/gpt-live", "/audio-stream"))
        twin = source.startswith("Features/DigitalTwin/")
        # Only the JPEG upload needs the larger existing Twin media body cap.
        body_limit = "256k;" if template == "/v1/twin/instances/{id}/media/photo" else "32k;" if twin else "4m;"
        upstream = CONTRACT["upstreams"].get(source, CONTRACT["upstreams"]["default"])
        lines.extend([
            'location ~ "^' + route_pattern(template) + '$" {',
            # nginx locations match a decoded path; reject escapes in the raw path.
            '    if ($request_uri ~ "^[^?]*%") { return 404; }',
            '    if ($request_method !~ "^(?:' + "|".join(sorted(methods)) + ')$") { return 405; }',
            "    client_max_body_size " + body_limit,
            "    access_log off;",
            "    proxy_pass " + upstream + ";",
            "    proxy_http_version 1.1;",
            "    proxy_set_header Host " + host + ";",
            "    proxy_set_header X-Forwarded-Proto https;",
            "    proxy_set_header X-Forwarded-For $remote_addr;",
            "    proxy_set_header X-Forwarded-Host '';",
            "    proxy_set_header Forwarded '';",
            "    proxy_set_header Upgrade $http_upgrade;",
            "    proxy_set_header Connection $keepsake_connection_upgrade;",
            "    proxy_cache off;",
            "    proxy_connect_timeout 10s;",
            "    proxy_read_timeout " + ("900s;" if streaming else "30s;" if twin else "65s;"),
            "    proxy_send_timeout 65s;",
        ])
        if streaming:
            lines.extend(["    proxy_buffering off;", "    proxy_request_buffering off;"])
        lines.extend(['    add_header Cache-Control "no-store" always;', '    add_header Referrer-Policy "no-referrer" always;', "}"])
    lines.extend([
        "# Explicit existing services retain their own backend authentication.",
        "# Preserve the existing /ios/ and CloudVPSBridge locations in the server shell.",
        "location = /.well-known/apple-app-site-association {",
        '    if ($request_uri !~ "^/[.]well-known/apple-app-site-association(?:[?]|$)") { return 404; }',
        '    if ($request_method !~ "^(GET|HEAD)$") { return 405; }',
        "    proxy_pass http://127.0.0.1:5080;",
        "    proxy_set_header Host " + host + ";",
        "    proxy_set_header X-Forwarded-Proto https;",
        "    proxy_set_header X-Forwarded-Host '';",
        "    proxy_set_header Forwarded '';",
        "    proxy_cache off;",
        '    add_header Cache-Control "no-store" always;',
        '    add_header X-Content-Type-Options "nosniff" always;',
        "}",
        "location ^~ /app/v1/ {",
        '    if ($request_uri ~ "^[^?]*%") { return 404; }',
        "    access_log off;",
        "    client_max_body_size 256k;",
        "    proxy_pass http://127.0.0.1:5080;",
        "    proxy_http_version 1.1;",
        "    proxy_set_header Upgrade $http_upgrade;",
        "    proxy_set_header Connection $keepsake_connection_upgrade;",
        "    proxy_read_timeout 65s;",
        "    proxy_send_timeout 65s;",
        "    proxy_buffering off;",
        "    proxy_request_buffering off;",
        "    proxy_set_header Host " + host + ";",
        "    proxy_set_header X-Forwarded-Proto https;",
        "    proxy_set_header X-Forwarded-For $remote_addr;",
        "    proxy_set_header X-Forwarded-Host '';",
        "    proxy_set_header Forwarded '';",
        "    proxy_cache off;",
        '    add_header Cache-Control "no-store" always;',
        '    add_header Referrer-Policy "no-referrer" always;',
        "}",
        "location /v1/ { access_log off; return 404; }",
        "location = /v1 { return 404; }",
        "location / { return 404; }",
    ])
    return "\n".join(lines) + "\n"


def allowed(method, path):
    return any(method in methods and re.fullmatch(pattern, path) for pattern, methods in ROUTES)


def render(host, guest_only=True):
    if not re.fullmatch(r"[A-Za-z0-9.-]+", host):
        raise ValueError("Require canonical accepted host without port/path")
    lines = [
        "# Generated by keepsake_proxy.py; include inside the canonical HTTPS server.",
        "# " + ("Guest-only snippet: replace the legacy catch-all separately." if guest_only else "Complete source-owned compatibility locations; default route denies unknown paths."),
    ]
    # Fragment claim links load the same app directly. Redirects can lose claim
    # handling order, and a broad prefix would accidentally serve API callbacks.
    for route in ('/keepsake', '/keepsake/', '/keepsake/index.html',
                  '/keepsake/duel.html', '/keepsake/chronicle.html',
                  '/keepsake/evening', '/keepsake/evening/', '/keepsake/evening/index.html'):
        lines.extend([
            'location = ' + route + ' {',
            '    if ($request_uri !~ "^' + re.escape(route).replace(r'\.', '[.]') + r'(?:[?]|$)") { return 404; }',
            '    if ($request_method !~ "^(GET|HEAD)$") { return 405; }',
            '    root /var/www/dildovander/current;',
            '    try_files /index.html =404;',
            "    add_header Content-Security-Policy \"frame-ancestors 'none'\" always;",
            '    access_log off;',
            '    add_header Cache-Control "no-store" always;',
            '    add_header Referrer-Policy "no-referrer" always;',
            '    add_header X-Robots-Tag "noindex, nofollow" always;',
            '    add_header X-Content-Type-Options "nosniff" always;',
            '}',
        ])
    for pattern, methods in ROUTES:
        # Quoting is required because nginx otherwise treats GUID {n} braces as blocks.
        lines.extend([
            'location ~ "^' + pattern + '$" {',
            '    if ($request_uri ~ "^[^?]*%") { return 404; }',
            '    if ($request_method !~ "^(?:' + "|".join(sorted(methods)) + ')$") { return 405; }',
            "    client_max_body_size 16m;",
            "    access_log off;",
            "    proxy_pass http://127.0.0.1:5080;",
            "    proxy_http_version 1.1;",
            "    proxy_set_header Host " + host + ";",
            "    proxy_set_header X-Forwarded-Proto https;",
            "    proxy_set_header X-Forwarded-For $remote_addr;",
            "    proxy_set_header Forwarded '';",
            "    proxy_set_header X-Forwarded-Host '';",
            "    proxy_cache off;",
            "    proxy_connect_timeout 10s;",
            "    proxy_read_timeout 65s;",
            "    proxy_send_timeout 65s;",
            '    add_header Cache-Control "no-store" always;',
            '    add_header Referrer-Policy "no-referrer" always;',
            "}",
        ])
    lines.extend([
        "# Plain prefix allows the anchored regex routes above; ^~ would disable them.",
        "location /guest/ { access_log off; return 404; }",
        "location = /guest { return 404; }",
        "# Unknown API routes must not reach an SPA fallback.",
        "location /api/ { return 404; }",
        "location = /api { return 404; }",
    ])
    return "\n".join(lines) + "\n"


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--compatibility", action="store_true")
    parser.add_argument("--http-map-output", type=Path)
    args = parser.parse_args()
    if args.compatibility and args.http_map_output is None:
        parser.error("--compatibility requires --http-map-output for the http-scope Upgrade map")
    args.output.write_text(render_compatibility(args.host) if args.compatibility else render(args.host), encoding="utf-8")
    if args.http_map_output is not None:
        args.http_map_output.write_text(render_http_map(), encoding="utf-8")
