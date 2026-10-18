"""Notebook/helper checks and real server tests for opt-in Colab proxy access."""
import argparse
import io
import json
import os
from pathlib import Path
import socket
import tempfile
import unittest
import urllib.error
import urllib.request
import zipfile

import colab_runtime as runtime

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--exe', type=Path, default=ROOT / ('Amuse.Web/bin/Release/net10.0-windows10.0.17763.0/Amuse.Web.exe' if os.name == 'nt' else 'Amuse.Web/bin/linux/Release/net10.0/linux-x64/Amuse.Web'))
args = parser.parse_args()
EXE = args.exe.resolve()
PROXY, FRAME = 'https://kernel-8080.example.test', 'https://output.example.test'

def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]

class ColabTests(unittest.TestCase):
    def test_notebook_cells_compile_and_have_no_saved_outputs(self):
        notebook = json.loads((ROOT / 'Notebooks/Amuse_Web_Colab.ipynb').read_text(encoding='utf-8'))
        self.assertEqual(notebook['nbformat'], 4)
        code = '\n'.join(''.join(cell['source']) for cell in notebook['cells'] if cell['cell_type'] == 'code')
        self.assertNotIn('proxyPort', code)
        self.assertNotIn('serve_kernel_port', code)
        self.assertIn('amuse.CloudflareTunnel(WORK, PORT).start()', code)
        for cell in notebook['cells']:
            if cell['cell_type'] == 'code':
                compile(''.join(cell['source']), cell['id'], 'exec')
                self.assertEqual(cell['outputs'], [])
                self.assertIsNone(cell['execution_count'])

    def test_source_extraction_and_path_traversal(self):
        with tempfile.TemporaryDirectory() as temp:
            temp = Path(temp)
            good = temp / 'good.zip'
            with zipfile.ZipFile(good, 'w') as archive:
                archive.writestr('Scripts/colab_runtime.py', '# fixture')
            self.assertTrue((runtime.extract_source(good, temp/'source')/'Scripts/colab_runtime.py').is_file())
            with self.assertRaises(FileExistsError):
                runtime.extract_source(good, temp/'source')
            for name in ('../outside.py', '/absolute.py'):
                bad = temp / 'bad.zip'
                with zipfile.ZipFile(bad, 'w') as archive:
                    archive.writestr(name, 'bad')
                with self.assertRaises(ValueError):
                    runtime.extract_source(bad, temp/'bad-source')

    def test_exact_origin_validation(self):
        self.assertEqual(runtime.https_origin(PROXY + '/'), PROXY)
        for origin in ('http://host', 'https://user:pass@host', 'https://host/path', 'https://host/?q=1', 'null'):
            with self.assertRaises(ValueError):
                runtime.https_origin(origin)

    def test_occupied_port_is_not_stopped(self):
        with tempfile.TemporaryDirectory() as temp, socket.socket() as occupant:
            occupant.bind(('127.0.0.1', 0))
            occupant.listen()
            server = runtime.Server(EXE.parent, temp, occupant.getsockname()[1])
            with self.assertRaisesRegex(RuntimeError, 'occupied'):
                server.start()
            self.assertIsNone(server.process)
            self.assertGreater(occupant.fileno(), 0)

    def exercise_server(self, proxy):
        with tempfile.TemporaryDirectory() as temp:
            server = runtime.Server(EXE.parent, temp, port(), proxy_url=PROXY if proxy else None, frame_origin=FRAME if proxy else None)
            try:
                server.start()
                def request(path, headers=None, method='GET'):
                    req = urllib.request.Request(server.base+path, headers=headers or {}, method=method)
                    try:
                        with urllib.request.urlopen(req, timeout=10) as response:
                            return response.status, response.headers, response.read()
                    except urllib.error.HTTPError as error:
                        with error:
                            return error.code, error.headers, error.read()
                headers = {'Host': 'kernel-8080.example.test', 'Origin': PROXY}
                self.assertEqual(request('/api/health', headers)[0], 200 if proxy else 403)
                self.assertEqual(request('/api/health', {'Origin': 'https://evil.example'})[0], 403)
                self.assertEqual(request('/api/health', {'Host': 'other.example.test'})[0], 403)
                self.assertEqual(request('/api/health', {'Origin': 'http://kernel-8080.example.test:'+str(server.port), 'Host': 'kernel-8080.example.test'})[0], 403)
                navigation = {**headers, 'Sec-Fetch-Site': 'cross-site', 'Sec-Fetch-Mode': 'navigate', 'Sec-Fetch-Dest': 'iframe'}
                status, response, body = request('/', navigation)
                self.assertEqual(status, 200 if proxy else 403)
                if proxy:
                    self.assertIn(FRAME, response['Content-Security-Policy'])
                    self.assertNotIn('*', response['Content-Security-Policy'])
                    self.assertIn(b'<html', body)
                    self.assertEqual(request('/api/session', navigation)[0], 403)
                    self.assertEqual(request('/app.js', {**headers, 'Sec-Fetch-Site':'cross-site'})[0], 403)
                    self.assertEqual(request('/api/jobs/missing/cancel', headers, 'POST')[0], 403)
                    self.assertEqual(request('/api/jobs/missing/cancel', {**headers,'X-Amuse-Token':server.token}, 'POST')[0], 404)
                else:
                    _, response, _ = request('/')
                    self.assertIn("frame-ancestors 'none'", response['Content-Security-Policy'])
            finally:
                server.stop()

    def test_default_local_access_is_preserved(self):
        self.exercise_server(False)

    def test_opt_in_proxy_iframe_and_csrf(self):
        self.exercise_server(True)

    def test_sample_request_uses_installed_model_and_selected_device(self):
        calls = []
        class Fake:
            def request(self, path, data=None):
                calls.append((path, data))
                if path == '/api/catalog':
                    return {'devices':[{'id':7,'vendor':'Nvidia'}], 'models':[{'kind':'diffusion','id':550,'status':'Installed'}]}
                return {'id':'fixture'}
            def wait_job(self, job):
                return job
        runtime.sample_image(Fake(), 'original teapot')
        request = calls[-1][1]
        self.assertEqual(request['deviceId'], 7)
        self.assertEqual(request['options']['guidanceScale'], 1)
        self.assertNotIn('download', ' '.join(path for path,_ in calls))

if __name__ == '__main__':
    unittest.main(argv=['test-colab.py'], verbosity=2)
