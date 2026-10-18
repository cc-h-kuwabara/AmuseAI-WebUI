"""Integration checks against a disposable Amuse Web process; Python standard library only.
Run after a Release build: python scripts/test-web.py
Does not download models or change desktop settings.
"""
import argparse
import io
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid
import wave

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--exe', type=Path, default=ROOT / ('Amuse.Web/bin/Release/net10.0-windows10.0.17763.0/Amuse.Web.exe' if os.name == 'nt' else 'Amuse.Web/bin/Release/net10.0/Amuse.Web'))
args = parser.parse_args()
exe = args.exe.resolve()
assert exe.is_file(), 'Build Amuse.Web in Release first.'
results = []


def passed(name):
    results.append(name)
    print('PASS:', name, flush=True)


def free_port():
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0))
        return s.getsockname()[1]


port = free_port()
base = f'http://localhost:{port}'
token = ''


def request(path, data=None, method=None, headers=None, raw=False):
    h = dict(headers or {})
    if data is not None and not isinstance(data, bytes):
        data = json.dumps(data).encode()
        h['Content-Type'] = 'application/json'
    if method and method not in ('GET', 'HEAD') and 'X-Amuse-Token' not in h:
        h['X-Amuse-Token'] = token
    with urllib.request.urlopen(urllib.request.Request(base + path, data=data, headers=h, method=method), timeout=30) as response:
        body = response.read()
        return json.loads(body) if not raw and body and 'application/json' in response.headers.get('Content-Type', '') else body


def expect_status(status, path, **kwargs):
    try:
        request(path, **kwargs)
    except urllib.error.HTTPError as error:
        assert error.code == status, (path, error.code, error.read())
    else:
        raise AssertionError(f'{path} did not return {status}')


def upload(name, content):
    boundary = 'AmuseWebIntegrationBoundary'
    body = (f'--{boundary}\r\nContent-Disposition: form-data; name="files"; filename="{name}"\r\nContent-Type: application/octet-stream\r\n\r\n').encode() + content + f'\r\n--{boundary}--\r\n'.encode()
    return request('/api/files', body, 'POST', {'Content-Type': 'multipart/form-data; boundary=' + boundary})[0]


def wait_job(job, expected='completed'):
    for _ in range(300):
        state = request('/api/jobs/' + job['id'])
        if state['status'] not in ('queued', 'running', 'canceling'):
            assert state['status'] == expected, state
            return state
        time.sleep(.1)
    raise AssertionError('Job did not finish: ' + job['id'])


for invalid in (['--port', '0'], ['--port', '65536'], ['--port', 'bad'], ['--port'], ['--urls', 'http://0.0.0.0:9000'], ['--port', '8080', '--port', '8081']):
    output = subprocess.run([str(exe), *invalid], capture_output=True, timeout=20)
    assert output.returncode != 0, invalid
passed('invalid and duplicate command-line options')

with tempfile.TemporaryDirectory(prefix='amuse-web-test-') as temp:
    directory = Path(temp)
    process = None
    log = (directory / 'server.log').open('wb')

    def start():
        global token
        forbidden_port = free_port()
        env = dict(os.environ, ASPNETCORE_URLS=f'http://0.0.0.0:{forbidden_port}',
                   Kestrel__Endpoints__Injected__Url=f'http://0.0.0.0:{forbidden_port}')
        proc = subprocess.Popen([str(exe), '--port', str(port), '--data-dir', str(directory / 'data')], stdout=log, stderr=log, env=env, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        for _ in range(100):
            if proc.poll() is not None:
                raise AssertionError('Server exited: ' + (directory / 'server.log').read_text(errors='replace'))
            try:
                token = request('/api/session')['token']
                with socket.socket() as check:
                    assert check.connect_ex(('127.0.0.1', forbidden_port)) != 0, 'An environment-defined listener was unexpectedly opened.'
                return proc
            except (OSError, urllib.error.URLError):
                time.sleep(.1)
        raise AssertionError('Server did not start')

    try:
        process = start()
        assert request('/api/health')['port'] == port
        assert request('/api/health')['localOnly'] is True
        assert b'<html lang="ja">' in request('/')
        assert b'submitGenerate' in request('/app.js')
        passed('custom port, static assets and health')
        expect_status(403, '/api/health', headers={'Host': f'evil.example:{port}'})
        expect_status(403, '/api/health', headers={'Origin': 'http://evil.example'})
        expect_status(403, '/api/health', headers={'Sec-Fetch-Site': 'cross-site'})
        expect_status(403, '/api/jobs/nope/cancel', method='POST', headers={'X-Amuse-Token': ''})
        passed('Host/Origin/Fetch-Site and mutation token protection')
        duplicate = subprocess.run([str(exe), '--port', str(free_port()), '--data-dir', str(directory / 'other')], capture_output=True, timeout=20)
        assert duplicate.returncode != 0
        passed('desktop/Web host instance exclusion')
        catalog = request('/api/catalog')
        assert catalog['models'] and catalog['tools']
        passed('model catalog, tool service and GPU discovery response')
        if os.name != 'nt':
            assert any(x['vendor'] == 'CPU' for x in catalog['devices']), catalog
            cuda_index = next(i for i, x in enumerate(catalog['environments']) if x['environment'] == 'linux-cuda')
            failure = wait_job(request(f'/api/environments/{cuda_index}/create', {}, 'POST'), 'failed')
            assert 'install-sd-cuda-linux.sh' in failure['error'], failure
            cuda_directory = directory / 'data/RuntimeStableDiffusionCpp/linux-cuda'
            cuda_directory.mkdir(parents=True)
            marker = cuda_directory / 'preserve.txt'
            marker.write_text('existing locally built runtime')
            failure = wait_job(request(f'/api/environments/{cuda_index}/rebuild', {}, 'POST'), 'failed')
            assert 'locally built environment' in failure['error'] and marker.exists(), failure
            passed('Linux CPU fallback, CUDA preparation error and protection of locally built runtime')
        icon = (ROOT / 'Amuse.App/Images/Icon.png').read_bytes()
        image = upload('sample.png', icon)
        assert request('/api/files/' + image['id'], raw=True) == icon
        expect_status(400, '/api/files/not-a-file-id')
        passed('upload, file ID validation and byte-exact download')
        request('/api/files/' + image['id'], method='DELETE')
        assert image['id'] in [x['id'] for x in request('/api/trash')]
        expect_status(404, '/api/files/' + image['id'])
        request('/api/trash/' + image['id'] + '/restore', method='POST')
        assert request('/api/files/' + image['id'], raw=True) == icon
        passed('recoverable deletion and restoration')
        settings = request('/api/config/general')
        wait_job(request('/api/config/general', {**settings, 'historyItems': 42}, 'PUT'))
        assert request('/api/config/general')['historyItems'] == 42
        wait_job(request('/api/config/general', {'historyItems': 43, 'readBuffer': -1}, 'PUT'), 'failed')
        assert request('/api/config/general')['historyItems'] == 42
        assert len(request('/api/catalog')['devices']) == len(catalog['devices'])
        passed('queued settings persistence and rollback on invalid edit')
        tokens = request('/api/config/tokens')
        wait_job(request('/api/config/tokens', [{'name': tokens[0]['name'], 'token': 'integration-test-secret'}], 'PUT'))
        listed = request('/api/config/tokens')
        assert listed[0]['configured'] and 'integration-test-secret' not in json.dumps(listed)
        passed('token update without reading credentials back')
        components = request('/api/config/component')
        local = {'id': 900001, 'name': 'Test local component', 'key': 'WEB_TEST_LOCAL', 'checkpoint': {'name': 'Local file', 'type': 'LocalFile', 'path': str(ROOT / 'Amuse.App/Images/Icon.png')}}
        wait_job(request('/api/config/component', [*components, local], 'PUT'))
        wait_job(request('/api/models/component/900001/download', method='POST'))
        passed('download service dependency wiring with a local checkpoint (no network)')
        sound = io.BytesIO()
        with wave.open(sound, 'wb') as wav:
            wav.setnchannels(1); wav.setsampwidth(2); wav.setframerate(16000); wav.writeframes(b'\x00\x00' * 16000)
        audio = upload('silence.wav', sound.getvalue())
        composition = {'clips': [{'file': image['id'], 'duration': .5}], 'width': 64, 'height': 64, 'frameRate': 12}
        job = wait_job(request('/api/compose', composition, 'POST'))
        assert b'ftyp' in request('/api/files/' + job['files'][0], raw=True)[:64]
        encoded = directory / 'encoded.mp4'
        encoded.write_bytes(request('/api/files/' + job['files'][0], raw=True))
        probe = subprocess.run([str(exe.parent / 'ffprobe.exe') if os.name == 'nt' else 'ffprobe', '-v', 'error', '-select_streams', 'v:0', '-show_entries', 'stream=codec_name', '-of', 'json', str(encoded)], capture_output=True, check=True)
        assert json.loads(probe.stdout)['streams'][0]['codec_name'] == 'h264'
        passed('native image-to-MP4 encoding')
        audio_job = wait_job(request('/api/compose', {**composition, 'audio': audio['id']}, 'POST'))
        assert b'soun' in request('/api/files/' + audio_job['files'][0], raw=True)
        passed('native MP4 composition with audio track')
        wait_job(request('/api/compose', {**composition, 'clips': [{'file': job['files'][0], 'duration': .25}]}, 'POST'))
        passed('video clip decoding and trimmed export')
        long_job = request('/api/compose', {**composition, 'frameRate': 120, 'clips': [{'file': image['id'], 'duration': 600}]}, 'POST')
        canceled = request('/api/config/general', {'historyItems': 99}, 'PUT')
        request('/api/jobs/' + canceled['id'] + '/cancel', method='POST')
        request('/api/jobs/' + long_job['id'] + '/cancel', method='POST')
        wait_job(long_job, 'canceled'); wait_job(canceled, 'canceled')
        assert request('/api/config/general')['historyItems'] == 42
        passed('running and queued job cancellation without applying canceled settings')
        request('/api/server/stop', method='POST')
        assert process.wait(timeout=30) == 0
        passed('graceful shutdown with GPU service disposal')
        interrupted = uuid.uuid4().hex
        job_directory = directory / 'data/Jobs'
        (job_directory / (interrupted + '.json')).write_text(json.dumps({'id': interrupted, 'kind': 'test', 'status': 'running', 'created': '2026-10-18T00:00:00Z'}))
        (job_directory / 'corrupt.json').write_text('{broken')
        process = start()
        assert request('/api/config/general')['historyItems'] == 42
        assert request('/api/jobs/' + job['id'])['status'] == 'completed'
        assert request('/api/files/' + image['id'], raw=True) == icon
        passed('settings, jobs and files survive server restart')
        assert request('/api/jobs/' + interrupted)['status'] == 'interrupted'
        assert (job_directory / 'corrupt.json.corrupt').exists()
        passed('interrupted job recovery and corrupt manifest quarantine')
        request('/api/server/stop', method='POST')
        assert process.wait(timeout=30) == 0
        with socket.socket() as blocker:
            blocker.bind(('127.0.0.1', 0)); blocker.listen()
            conflict = subprocess.run([str(exe), '--port', str(blocker.getsockname()[1]), '--data-dir', str(directory / 'port-conflict')], capture_output=True, timeout=30)
            assert conflict.returncode != 0
        passed('occupied port fails without selecting another port')
        corrupt_data = directory / 'corrupt-settings'
        corrupt_data.mkdir()
        (corrupt_data / 'Settings.json').write_bytes(b'{not valid JSON')
        corrupt = subprocess.run([str(exe), '--port', str(free_port()), '--data-dir', str(corrupt_data)], capture_output=True, timeout=30)
        assert corrupt.returncode != 0 and (corrupt_data / 'Settings.json').read_bytes() == b'{not valid JSON'
        passed('corrupt settings are rejected without overwriting user data')
    except Exception:
        log.flush()
        print((directory / 'server.log').read_text(errors='replace')[-12000:])
        raise
    finally:
        if process and process.poll() is None:
            try:
                request('/api/server/stop', method='POST')
                process.wait(timeout=30)
            except Exception:
                process.kill(); process.wait(timeout=10)
        log.close()
print(f'{len(results)} integration checks passed. Real AI inference and browser rendering require separate validation.')
