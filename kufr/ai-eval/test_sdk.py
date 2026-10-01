import sys
import types
import unittest
from unittest.mock import patch
from evaluate import call_openai

class FakeError(Exception):
    status_code = 429

class FakeResponse:
    chunks = [b'{"status":"completed"}']
    def __enter__(self): return self
    def __exit__(self, *args): pass
    def iter_bytes(self, **kwargs): yield from self.chunks

class FakeClient:
    options = request = None
    def __init__(self, **options):
        FakeClient.options = options
        self.responses = types.SimpleNamespace(with_streaming_response=self)
    def __enter__(self): return self
    def __exit__(self, *args): pass
    def create(self, **request):
        FakeClient.request = request
        return FakeResponse()

class SdkTests(unittest.TestCase):
    def test_sdk_has_no_retries_and_preserves_request(self):
        sdk = types.SimpleNamespace(OpenAI=FakeClient, APIError=FakeError)
        with patch.dict(sys.modules, {'openai': sdk}):
            request = {'model': 'test', 'store': False, 'service_tier': 'default'}
            self.assertEqual(call_openai(request, 'test-only'), {'status': 'completed'})
            self.assertEqual(FakeClient.options['max_retries'], 0)
            self.assertEqual(FakeClient.options['timeout'], 45)
            self.assertEqual(FakeClient.request, request)
            with patch.object(FakeResponse, 'chunks', [b'x' * 65537]):
                with self.assertRaises(RuntimeError): call_openai(request, 'test-only')
