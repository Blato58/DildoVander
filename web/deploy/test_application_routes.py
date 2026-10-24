"""Static routing allowlist checks for the canonical application origin."""
from pathlib import Path
import re
import unittest
import keepsake_proxy as proxy


class ApplicationRoutingTests(unittest.TestCase):
    def test_account_api_supports_duplex_persona_lab_streams(self):
        config = proxy.render_compatibility('keepsake.example.invalid')
        block = config.split('location ^~ /app/v1/ {', 1)[1].split('\n}', 1)[0]
        for directive in ('proxy_http_version 1.1;',
                          'proxy_set_header Upgrade $http_upgrade;',
                          'proxy_set_header Connection $keepsake_connection_upgrade;',
                          'proxy_read_timeout 65s;', 'proxy_send_timeout 65s;',
                          'proxy_buffering off;', 'proxy_request_buffering off;',
                          'access_log off;'):
            self.assertIn(directive, block)
        self.assertNotIn('try_files', block)

    def test_apple_association_is_an_exact_nonredirecting_backend_route(self):
        config = proxy.render_compatibility('keepsake.example.invalid')
        block = config.split('location = /.well-known/apple-app-site-association {', 1)[1].split('\n}', 1)[0]
        self.assertIn('GET|HEAD', block)
        self.assertIn('proxy_pass http://127.0.0.1:5080;', block)
        self.assertIn('proxy_set_header Host keepsake.example.invalid;', block)
        self.assertIn('no-store', block)
        self.assertIn('nosniff', block)
        self.assertNotIn('return 30', block)
        self.assertNotIn('try_files', block)

    def test_only_deliberate_page_routes_receive_the_app(self):
        config = proxy.render_compatibility('keepsake.example.invalid')
        pages = set(re.findall(r'location = ([^ ]+) \{\n(?:(?!\n}).)*?try_files /index.html =404;', config, re.DOTALL))
        for route in ('/', '/suitcase', '/suitcase/diagnostics', '/suitcase/director', '/connections', '/workshop',
                      '/archive', '/archive/duel', '/archive/chronicle', '/signin', '/settings', '/settings/admin', '/persona-lab', '/keepsake/', '/keepsake/evening/'):
            self.assertIn(route, pages)
        for route in ('/app/index.html', '/companion/index.html', '/simulator/index.html',
                      '/workshop/index.html', '/workshop/viewer.html', '/keepsake/index.html', '/keepsake/evening/index.html',
                      '/keepsake/duel.html', '/keepsake/chronicle.html'):
            self.assertIn(route, pages)
        for route in ('/app/v1/session', '/guest/v1/claim', '/auth/callback', '/ios/manifest.plist',
                      '/workshop/model.zip', '/v1/unknown', '/assets/missing.js', '/unknown', '/keepsake/other.html'):
            self.assertNotIn(route, pages)
        self.assertNotIn('try_files $uri /index.html', config)
        self.assertIn('location / { return 404; }', config)
        self.assertIn('location ^~ /app/v1/', config)

    def test_all_static_vue_entries_have_an_exact_server_page(self):
        config = proxy.render_compatibility('keepsake.example.invalid')
        pages = set(re.findall(r'location = ([^ ]+) \{\n(?:(?!\n}).)*?try_files /index.html =404;', config, re.DOTALL))
        router = Path(__file__).resolve().parents[2] / 'mobilni-aplikace/src/application/router.ts'
        routes = set(re.findall(r"\{\s*path:\s*'([^']+)'", router.read_text(encoding='utf-8')))
        static_entries = {route for route in routes if ':' not in route}
        self.assertFalse(static_entries - pages, 'Vue entries missing from exact server ingress: ' + str(static_entries - pages))

    def test_customer_pages_are_private_get_only_and_do_not_catch_apis(self):
        config = proxy.render_compatibility('keepsake.example.invalid')
        for route in ('/account-deletion', '/me', '/me/', '/me/character', '/me/conversation', '/me/keepsakes', '/me/account', '/tonight', '/admin/customer-sessions', '/testing-sessions'):
            with self.subTest(route=route):
                block = config.split('location = ' + route + ' {', 1)[1].split('\n}', 1)[0]
                for value in ('GET|HEAD', 'no-store', 'no-referrer', 'noindex, nofollow', 'access_log off;', 'try_files /index.html =404;'):
                    self.assertIn(value, block)
                self.assertIn("Content-Security-Policy \"frame-ancestors 'none'\"", block)
                self.assertNotIn('proxy_pass', block)
                self.assertNotIn('return 30', block)
                pattern = re.search(r'if \(\$request_uri !~ "([^"]+)"', block).group(1)
                self.assertIsNotNone(re.search(pattern, route + '?return=%2F'))
                encoded = '/%' + format(ord(route[1]), '02x') + route[2:]
                for raw in (encoded, '/' + route, '/app/v1/../..' + route, route + '/extra'):
                    self.assertIsNone(re.search(pattern, raw), raw)

    def test_demo_is_an_exact_public_get_only_page(self):
        config = proxy.render_compatibility('keepsake.example.invalid')
        block = config.split('location = /demo {', 1)[1].split('\n}', 1)[0]
        for directive in ('GET|HEAD', 'try_files /index.html =404;', 'no-store', 'no-referrer', 'nosniff'):
            self.assertIn(directive, block)
        self.assertNotIn('proxy_pass', block)
        self.assertNotIn('X-Robots-Tag', block)
        pattern = re.search(r'if \(\$request_uri !~ "([^"]+)"', block).group(1)
        self.assertIsNotNone(re.search(pattern, '/demo?theme=ollivander'))
        for raw in ('/%64emo', '//demo', '/demo/extra', '/app/v1/../../demo'):
            self.assertIsNone(re.search(pattern, raw), raw)

    def test_guest_shell_load_preserves_claim_and_private_headers(self):
        config = proxy.render_compatibility('keepsake.example.invalid')
        for route in ('/archive/duel', '/archive/chronicle',
                      '/keepsake', '/keepsake/', '/keepsake/index.html', '/keepsake/duel.html', '/keepsake/chronicle.html',
                      '/keepsake/evening', '/keepsake/evening/', '/keepsake/evening/index.html'):
            block = config.split('location = ' + route + ' {', 1)[1].split('\n}', 1)[0]
            for header in ('no-store', 'no-referrer', 'noindex, nofollow'):
                self.assertIn(header, block)
            self.assertNotIn('return 30', block)
            self.assertIn('return 405', block)
            self.assertIn("Content-Security-Policy \"frame-ancestors 'none'\"", block)

    def test_immutable_assets_and_models_have_no_spa_fallback(self):
        config = Path(__file__).with_name('site-locations.conf').read_text()
        for route in ('/assets/', '/models/bundles/'):
            block = config.split('location ^~ ' + route, 1)[1].split('\n}', 1)[0]
            self.assertIn('immutable', block)
            self.assertIn('try_files $uri =404;', block)
        self.assertIn('location = /models/active.json', config)
        self.assertNotIn('location / {', config)

    def test_normalized_api_paths_cannot_become_page_fallback(self):
        config = proxy.render_compatibility('keepsake.example.invalid')
        block = config.split('location = /suitcase {', 1)[1].split('\n}', 1)[0]
        pattern = re.search(r'\$request_uri !~ "([^"]+)"', block).group(1)
        self.assertIsNotNone(re.search(pattern, '/suitcase?diagnostic=%2F'))
        for raw in ('/app/v1/../../suitcase', '/guest/v1/../../suitcase', '/%73uitcase', '//suitcase'):
            self.assertIsNone(re.search(pattern, raw), raw)

    def test_immutable_model_location_serves_json_independently_of_filename(self):
        config = proxy.render_compatibility('keepsake.example.invalid')
        match = re.search(r'^location \^~ /models/bundles/ \{\n(.*?)^\}', config, re.MULTILINE | re.DOTALL)
        self.assertIsNotNone(match)
        block = match.group(1)
        self.assertRegex(block, r'(?m)^    types\s*\{\s*\}\s*$')
        self.assertRegex(block, r'(?m)^    default_type application/json;\s*$')
        self.assertIn('nosniff', block)
        self.assertIn("default-src 'none'; sandbox", block)
        self.assertNotIn('text/html', block)


if __name__ == '__main__':
    unittest.main()
