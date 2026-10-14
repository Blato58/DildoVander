import importlib.util
from pathlib import Path
import unittest
import re


spec = importlib.util.spec_from_file_location("keepsake_proxy", Path(__file__).with_name("keepsake_proxy.py"))
proxy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proxy)
ID = "12345678-1234-1234-1234-123456789abc"


class ProxyTests(unittest.TestCase):
    def test_public_evening_is_get_only_and_not_an_owner_alias(self):
        self.assertTrue(proxy.allowed("GET", "/guest/v1/evening"))
        self.assertFalse(proxy.allowed("POST", "/guest/v1/evening"))
        asset = f"/guest/v1/evenings/{ID}/keepsakes/{ID}/editions/{ID}/assets/{ID}"
        self.assertTrue(proxy.allowed("GET", asset))
        self.assertFalse(proxy.allowed("DELETE", asset))
        self.assertFalse(proxy.allowed("POST", asset + "/claim"))

    def test_compatibility_map_matches_real_source_declarations(self):
        backend = Path(__file__).resolve().parents[2] / "backend"
        supplied = {(method, route) for method, route, source in proxy.CONTRACT["routes"]}
        actual = set()
        for path in [backend / "Program.cs", *sorted((backend / "Features").rglob("*.cs"))]:
            for method, route in re.findall(r'\.Map(Get|Post|Put|Delete|Patch)\("(/(?:v1/|healthz|twin)[^"\n]*)"', path.read_text(encoding="utf-8")):
                actual.add((method.upper(), route))
        self.assertEqual(supplied, actual, "Current backend routes must all be reviewed in the compatibility contract")
        for method, route, source in proxy.CONTRACT["routes"]:
            self.assertIn('Map' + method.title() + '("' + route + '"', (backend / source).read_text(encoding="utf-8"))

    def test_twin_ingress_keeps_its_isolated_listener_and_exact_methods(self):
        twin_source = "Features/DigitalTwin/TwinEndpoints.cs"
        self.assertEqual(49, sum(source != twin_source for method, route, source in proxy.CONTRACT["routes"]))
        self.assertEqual(11, sum(source == twin_source for method, route, source in proxy.CONTRACT["routes"]))
        paths = [
            ("GET", "/twin"), ("GET", "/healthz/twin"),
            ("POST", "/v1/twin/session"), ("DELETE", "/v1/twin/session"),
            ("GET", "/v1/twin/instances"), ("GET", "/v1/twin/health"),
            ("POST", "/v1/twin/device-token"),
        ]
        for instance in ("sim-development", "sim-integration", "kufr-68ee8f4b6048", "Twin_1.2", "a" * 96):
            paths.extend([
                ("GET", f"/v1/twin/instances/{instance}"),
                ("GET", f"/v1/twin/instances/{instance}/recording"),
                ("POST", f"/v1/twin/instances/{instance}/commands"),
                ("POST", f"/v1/twin/ingest/{instance}"),
            ])
        for method, path in paths:
            self.assertTrue(proxy.allowed_backend(method, path), (method, path))
            self.assertFalse(proxy.allowed(method, path), "Twin credentials must not become guest grants")
        for method, path in [
            ("POST", "/twin"), ("POST", "/healthz/twin"), ("GET", "/twin/"),
            ("GET", "/v1/twin/session"), ("GET", "/v1/twin/device-token"),
            ("GET", "/v1/twin/ingest/sim-development"),
            ("GET", "/v1/twin/instances/sim-development/commands"),
            ("POST", "/v1/twin/instances/live-device/recording"),
            ("GET", "/v1/twin/unknown"),
            ("GET", "/v1/twin/instances/" + "a" * 97),
            ("GET", "/v1/twin/instances/sim-development/recording/extra"),
            ("POST", "/v1/twin/ingest/sim-development/commands"),
            ("GET", "/v1/twin/instances/non%20canonical"),
            ("GET", "/guest/v1/twin/instances/sim-development"),
        ]:
            self.assertFalse(proxy.allowed_backend(method, path), (method, path))
            self.assertFalse(proxy.allowed(method, path), (method, path))

        config = proxy.render_compatibility("keepsake.example.invalid")
        blocks = config.split("\n}\n")
        for method, route, source in proxy.CONTRACT["routes"]:
            location = 'location ~ "^' + proxy.route_pattern(route) + '$" {'
            block = next(block for block in blocks if location in block)
            twin = source == twin_source
            upstream = "http://127.0.0.1:5082" if twin else "http://127.0.0.1:5080"
            self.assertIn("proxy_pass " + upstream + ";", block)
            if twin:
                self.assertIn("client_max_body_size 32k;", block)
                self.assertIn("proxy_read_timeout 30s;", block)
        self.assertNotIn("location ^~ /v1/twin/", config)
        self.assertNotIn("dildovander-twin.conf", config)

    def test_checked_in_examples_are_current_generator_outputs(self):
        deploy = Path(__file__).resolve().parent
        self.assertEqual(proxy.render_compatibility("keepsake.example.invalid"),
                         (deploy / "keepsake-compatibility.conf.example").read_text(encoding="utf-8"))
        self.assertEqual(proxy.render_http_map(),
                         (deploy / "keepsake-upgrade.conf.example").read_text(encoding="utf-8"))

    def test_compatibility_preserves_live_ota_and_operator_auth_boundaries(self):
        paths = [
            ("POST", "/v1/device-access/token"), ("GET", "/v1/admin/logs"),
            ("PUT", "/v1/admin/modes"), ("GET", "/v1/admin/memory"),
            ("GET", "/v1/firmware/manifests/stable.json"),
            ("GET", "/v1/firmware/artifacts/" + "a" * 64 + ".bin"),
            ("GET", "/v1/audio/" + ID), ("DELETE", "/v1/operations/" + ID),
            ("POST", f"/v1/conversations/{ID}/gpt-live"),
            ("POST", f"/v1/conversations/{ID}/realtime-live/{ID}/played"),
            ("POST", f"/v1/keepsakes/outcomes/{ID}"),
            ("GET", "/v1/admin/keepsakes"),
            ("POST", "/v1/admin/keepsake-deletions/preview"),
            ("POST", f"/v1/admin/keepsake-deletions/{ID}/confirm"),
            ("GET", f"/v1/admin/keepsake-deletions/batches/{ID}"),
            ("GET", f"/v1/admin/keepsake-deletions/receipts/{ID}"),
            ("POST", f"/v1/admin/keepsakes/{ID}/jobs/{ID}/cancel"),
            ("POST", f"/v1/admin/keepsakes/{ID}/revoke-shares"),
        ]
        for method, path in paths:
            self.assertTrue(proxy.allowed_backend(method, path), (method, path))
            self.assertFalse(proxy.allowed(method, path), "Legacy credentials must not become guest grants")
        for method, path in [
            ("POST", "/v1/admin/logs"), ("GET", "/v1/unknown"),
            ("GET", f"/v1/conversations/{ID}/gpt-live"),
            ("GET", "/v1/firmware/artifacts/" + "g" * 64 + ".bin"),
            ("GET", "/v1/firmware/manifests/unknown.json"),
            ("GET", f"/v1/admin/memory/records/{ID}/anything"),
        ]:
            self.assertFalse(proxy.allowed_backend(method, path), (method, path))
        config = proxy.render_compatibility("keepsake.example.invalid")
        self.assertIn("location / { return 404; }", config)
        self.assertIn("location /v1/ { access_log off; return 404; }", config)
        self.assertIn("proxy_buffering off;", config)
        self.assertIn("proxy_request_buffering off;", config)
        self.assertIn("proxy_set_header Upgrade $http_upgrade;", config)
        self.assertIn("proxy_set_header Connection $keepsake_connection_upgrade;", config)
        self.assertIn("if ($host != keepsake.example.invalid) { return 421; }", config)
        self.assertIn("location = / {", config)
        self.assertNotIn("location / {\n    proxy_pass", config)

    def test_exact_guest_routes_methods(self):
        for pattern, methods in proxy.ROUTES:
            # Exercise representative route expansions explicitly below.
            self.assertTrue(pattern.startswith("/guest/v1/"))
            self.assertTrue(methods)
        self.assertTrue(proxy.allowed("POST", "/guest/v1/claim"))
        self.assertTrue(proxy.allowed("GET", f"/guest/v1/keepsakes/{ID}/assets/{ID}"))
        self.assertTrue(proxy.allowed("DELETE", f"/guest/v1/keepsakes/{ID}/portrait"))
        self.assertTrue(proxy.allowed("POST", f"/guest/v1/keepsakes/{ID}/portraits/{ID}/cancel"))
        self.assertTrue(proxy.allowed("POST", f"/guest/v1/keepsakes/{ID}/generation/{ID}/cancel"))

    def test_negative_boundary(self):
        for method, path in [
            ("GET", "/guest/v1/claim"), ("OPTIONS", "/guest/v1/claim"),
            ("POST", "/v1/keepsakes/outcomes/" + ID),
            ("GET", "/v1/admin/logs"), ("GET", "/guest/v1/admin/logs"),
            ("GET", f"/guest/v1/keepsakes/{ID}/assets/{ID}/extra"),
            ("GET", f"/guest/v1/keepsakes/{ID}/assets/not-a-guid"),
            ("GET", "/api/anything"), ("GET", "/guest/v1/claim/"),
            ("GET", "/guest/v1/keepsakes/" + ID.upper()),
            ("GET", f"/guest/v1/keepsakes/{ID}/generation/{ID}/cancel"),
            ("POST", "/guest/v1/admin/keepsake-deletions/preview"),
        ]:
            with self.subTest(method=method, path=path):
                self.assertFalse(proxy.allowed(method, path))

    def test_canonical_host_and_no_cache(self):
        config = proxy.render("keepsake.example.invalid")
        self.assertIn("proxy_set_header Host keepsake.example.invalid;", config)
        self.assertNotIn("Host $host", config)
        self.assertIn("location /guest/", config)
        self.assertNotIn("location ^~ /guest/", config)
        self.assertIn("access_log off", config)
        self.assertIn("proxy_cache off", config)
        for host in ("https://host", "host;", "host\nsecret", "host:5080"):
            with self.assertRaises(ValueError):
                proxy.render(host)


if __name__ == "__main__":
    unittest.main()
