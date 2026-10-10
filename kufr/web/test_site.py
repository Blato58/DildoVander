import hashlib
import io
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile


def module(name):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + '.py'))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


builder = module('build-site')
publisher = module('publish-site')
reader = module('verify-site')
SOURCE_SHA = 'a' * 40


def payload(root):
    names = ['index.html', 'site.css', 'workshop/index.html', 'workshop/viewer.html',
             'workshop/F2-kouzelny-kufr.zip', 'companion/index.html', 'simulator/index.html',
             'design-system/arcane-workshop.css']
    root.mkdir()
    for name in names:
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(name.encode())
    manifest = {'schema': 1, 'source_sha': SOURCE_SHA, 'package': 'F2-kouzelny-kufr.zip',
                'sha256': {name: builder.digest(root / name) for name in names}}
    (root / 'site-manifest.json').write_text(json.dumps(manifest))


class PublicationTests(unittest.TestCase):
    @unittest.skipIf(os.name == 'nt', 'Atomic directory symlink replacement is a Linux deployment contract')
    def test_atomic_publish_retains_previous_release(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, site = root / 'source', root / 'site'
            payload(source)
            site.mkdir()
            self.assertEqual(publisher.publish(source, site, '100-1', SOURCE_SHA), '')
            self.assertEqual(publisher.publish(source, site, '101-1', SOURCE_SHA), '100-1')
            self.assertTrue((site / 'current').samefile(site / 'releases/101-1'))
            publisher.restore_activation(site, '101-1', '100-1')
            self.assertTrue((site / 'current').samefile(site / 'releases/100-1'))

    def test_first_activation_failure_restores_absent_current(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, site = root / 'source', root / 'site'
            payload(source)
            site.mkdir()
            previous = publisher.publish(source, site, '100-1', SOURCE_SHA)
            publisher.restore_activation(site, '100-1', previous)
            self.assertFalse((site / 'current').exists())
            self.assertFalse((site / 'current').is_symlink())
            self.assertTrue((site / 'releases/100-1/index.html').is_file())

    def test_failed_activation_rollback_refuses_an_unrelated_current(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, site = root / 'source', root / 'site'
            payload(source)
            site.mkdir()
            publisher.publish(source, site, '100-1', SOURCE_SHA)
            with self.assertRaisesRegex(ValueError, 'Current changed'):
                publisher.restore_activation(site, '101-1', '')
            self.assertTrue((site / 'current').samefile(site / 'releases/100-1'))

    def test_wrong_provenance_or_changed_payload_never_switches_current(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, site = root / 'source', root / 'site'
            payload(source)
            site.mkdir()
            publisher.publish(source, site, '100-1', SOURCE_SHA)
            with self.assertRaises(ValueError):
                publisher.publish(source, site, '101-1', 'b' * 40)
            (source / 'workshop/viewer.html').write_bytes(b'corrupt')
            with self.assertRaises(ValueError):
                publisher.publish(source, site, '101-1', SOURCE_SHA)
            self.assertTrue((site / 'current').samefile(site / 'releases/100-1'))

    def test_uninventoried_files_and_symlinks_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'source'
            payload(source)
            (source / 'obsolete.zip').write_bytes(b'old')
            with self.assertRaises(ValueError):
                publisher.verify(source, SOURCE_SHA)
            (source / 'obsolete.zip').unlink()
            try:
                (source / 'shortcut').symlink_to(source / 'index.html')
            except OSError as error:
                self.skipTest('Host does not permit symlinks: ' + str(error))
            with self.assertRaises(ValueError):
                publisher.verify(source, SOURCE_SHA)

    def test_release_path_traversal_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ValueError):
                publisher.switch(Path(directory), '../outside')


class CadPromotionTests(unittest.TestCase):
    def test_cad_zip_index_and_source_readback(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            out = root / 'exports'
            out.mkdir()
            (root / 'source.py').write_bytes(b'verified source')
            sources = {'source.py': builder.digest(root / 'source.py')}
            manifest = {'revision': 'F2', 'sources': sources}
            (out / 'manifest.json').write_text(json.dumps(manifest))
            (out / 'viewer.html').write_bytes(b'verified viewer')
            verified = {name: builder.digest(out / name) for name in ('manifest.json', 'viewer.html')}
            report = {'result': 'PASS_CAD_NOT_PHYSICAL_ACCEPTANCE', 'sources': sources,
                      'export_sha256': verified}
            (out / 'verification.json').write_text(json.dumps(report))
            hashes = {**verified, 'verification.json': builder.digest(out / 'verification.json')}
            (out / 'artifact-sha256.json').write_text(json.dumps(hashes))
            members = ['source.py', *['exports/' + name for name in hashes], 'exports/artifact-sha256.json']
            package = out / 'F2-kouzelny-kufr.zip'
            with zipfile.ZipFile(package, 'w') as archive:
                for name in members:
                    archive.write(root / name, name)
            (out / 'package-sha256.txt').write_text(builder.digest(package) + '  ' + package.name)
            self.assertEqual(builder.verified_exports(root)[0]['revision'], 'F2')
            (out / 'old.zip').write_bytes(b'unverified old export')
            self.assertNotIn('old.zip', builder.verified_exports(root)[1])
            (out / 'viewer.html').write_bytes(b'changed viewer')
            with self.assertRaises(ValueError):
                builder.verified_exports(root)
            (out / 'viewer.html').write_bytes(b'verified viewer')
            (root / 'source.py').write_bytes(b'changed source')
            with self.assertRaises(ValueError):
                builder.verified_exports(root)


class PublicReadbackTests(unittest.TestCase):
    def test_readback_checks_application_chunks_and_images(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / 'site'
            payload(root)
            manifest_path = root / 'site-manifest.json'
            manifest = json.loads(manifest_path.read_bytes())
            additions = {'companion/assets/app.js': b'application',
                         'workshop/images/open.png': b'image'}
            for name, data in additions.items():
                manifest['sha256'][name] = hashlib.sha256(data).hexdigest()
            manifest_path.write_text(json.dumps(manifest))
            requested = []

            def response(url, timeout):
                name = url.removeprefix('https://example.test/') or 'index.html'
                requested.append(name)
                data = additions.get(name)
                if name in ('ios/', 'healthz'):
                    data = b'ok'
                elif data is None:
                    data = (root / name).read_bytes()
                stream = io.BytesIO(data)
                stream.status = 200
                return stream

            with patch.object(reader, 'urlopen', side_effect=response):
                reader.verify('https://example.test', root)
            self.assertIn('companion/assets/app.js', requested)
            self.assertIn('workshop/images/open.png', requested)
            additions['companion/assets/app.js'] = b'stale application'
            with patch.object(reader, 'urlopen', side_effect=response):
                with self.assertRaisesRegex(ValueError, 'companion/assets/app.js'):
                    reader.verify('https://example.test', root)

    def test_static_preview_preserves_body_classes_and_disables_connection(self):
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory) / 'repo'
            final = repo / 'kufr/mechanika/final/exports'
            final.mkdir(parents=True)
            (final / 'viewer.html').write_text('viewer')
            (final / 'F2-kouzelny-kufr.zip').write_bytes(b'zip')
            design = repo / 'kufr/design-system'
            design.mkdir(parents=True)
            (design / 'arcane-workshop.css').write_text('css')
            simulator = repo / 'kufr/simulator'
            simulator.mkdir()
            (simulator / 'dashboard.html').write_text('<body class="aw-root">dashboard</body>')
            (simulator / 'dashboard.js').write_text('script')
            web = repo / 'kufr/web'
            web.mkdir()
            (web / 'index.html').write_text('__REVISION__ __PACKAGE_NAME__')
            (web / 'site.css').write_text('css')
            dist = repo / 'dist'
            dist.mkdir()
            (dist / 'index.html').write_text('<head></head><body>companion</body>')
            destination = repo / 'site'
            with patch.object(builder, 'verified_exports', return_value=({'revision': 'F2'},
                              ['viewer.html', 'F2-kouzelny-kufr.zip'])), \
                    patch.object(builder.subprocess, 'check_output', return_value=SOURCE_SHA):
                builder.build(repo, destination, dist)
            self.assertIn('<body class="aw-root" data-static-preview>',
                          (destination / 'simulator/index.html').read_text())
            self.assertIn('name="kufr-browser-mode" content="local-fake"',
                          (destination / 'companion/index.html').read_text())


if __name__ == '__main__':
    unittest.main()
