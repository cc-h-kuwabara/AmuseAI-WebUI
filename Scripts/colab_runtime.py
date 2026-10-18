"""Colab setup/server helpers. Standard library only; does not change notebook Python packages."""
from __future__ import annotations

import json
import os
from pathlib import Path
import platform
import re
import shutil
import socket
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
import colab_cache


def extract_source(archive, destination):
    destination = Path(destination).resolve()
    if destination.exists():
        raise FileExistsError(f"Source directory exists: {destination}. Reuse it or choose another path.")
    with zipfile.ZipFile(archive) as bundle:
        for entry in bundle.infolist():
            target = (destination / entry.filename).resolve()
            if not target.is_relative_to(destination) or (entry.external_attr >> 16) & 0o170000 == 0o120000:
                raise ValueError(f"Unsafe source archive entry: {entry.filename}")
        bundle.extractall(destination)
    if not (destination / 'Scripts/colab_runtime.py').is_file():
        raise ValueError("Select Amuse_Colab_Source.zip, with Scripts/colab_runtime.py at its root.")
    return destination


def run(command, *, env=None, cwd=None, log_file=None):
    log_file = Path(log_file) if log_file else Path(cwd or Path.cwd()) / 'build.log'
    log_file.parent.mkdir(parents=True, exist_ok=True)
    command = list(map(str, command))
    print(f'Starting: {command[0]} | Log: {log_file}', flush=True)
    started = time.monotonic()
    with log_file.open('ab', buffering=0) as log:
        log.write(('\n+ ' + ' '.join(command) + '\n').encode('utf-8'))
        with subprocess.Popen(command, env=env, cwd=cwd,
                              stdout=log, stderr=subprocess.STDOUT) as process:
            try:
                returncode = process.wait()
            except BaseException:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
                log.write(b'\nBuild command interrupted.\n')
                raise
        status = f'Exit code: {returncode}; elapsed: {time.monotonic() - started:.1f}s'
        log.write(('\n' + status + '\n').encode('utf-8'))
    if returncode:
        print(f'Failed ({returncode}). See log: {log_file}', flush=True)
        raise subprocess.CalledProcessError(returncode, command)
    print(f'Finished in {time.monotonic() - started:.1f}s | Log: {log_file}', flush=True)


def build_parallelism():
    cpus = len(os.sched_getaffinity(0)) if hasattr(os, 'sched_getaffinity') else os.cpu_count() or 2
    try:
        quota, period = Path('/sys/fs/cgroup/cpu.max').read_text().split()
        if quota != 'max':
            cpus = min(cpus, max(1, int(quota) // int(period)))
    except (OSError, ValueError):
        pass
    try:
        available = int(next(line.split()[1] for line in Path('/proc/meminfo').read_text().splitlines() if line.startswith('MemAvailable:'))) * 1024
        memory_jobs = max(1, int((available - 2 * 1024**3) // (2 * 1024**3)))
    except (OSError, StopIteration, ValueError):
        memory_jobs = 2
    return max(1, min(cpus, memory_jobs, 8))


def prepare(source, work, use_gpu=True, *, cache_directory=None, build_jobs=0):
    if not isinstance(build_jobs, int) or not 0 <= build_jobs <= 64:
        raise ValueError('BUILD_JOBS must be 0 (automatic) or an integer from 1 to 64.')
    source, work = Path(source).resolve(), Path(work).resolve()
    if platform.system() != 'Linux' or platform.machine() not in ('x86_64', 'amd64'):
        raise RuntimeError('Use a Linux x64 Colab runtime.')
    log_file = work / 'logs/build.log'

    def execute(command, **kwargs):
        run(command, log_file=log_file, **kwargs)

    native_platform = None
    if use_gpu:
        for tool in ('nvidia-smi', 'nvcc'):
            if not shutil.which(tool):
                raise RuntimeError(f'{tool} is missing. Select a GPU runtime, or set USE_GPU=False.')
        execute(['nvidia-smi', '--query-gpu=name,memory.total', '--format=csv,noheader'])
        native_platform = {
            'gpu': subprocess.check_output(['nvidia-smi', '--query-gpu=name', '--format=csv,noheader'], text=True).strip(),
            'compiler': subprocess.check_output(['nvcc', '--version'], text=True).strip(),
            'installer': colab_cache.digest_file(source / 'Scripts/install-sd-cuda-linux.sh')}
    work.mkdir(parents=True, exist_ok=True)
    if shutil.disk_usage(work).free < 8 * 1024**3:
        raise RuntimeError('At least 8 GiB free space is needed for building, plus space for the selected model.')
    cache = Path(cache_directory).expanduser().resolve() if cache_directory else None
    system = {'machine': platform.machine(), 'libc': platform.libc_ver(), 'os': platform.freedesktop_os_release().get('VERSION_ID')}
    app_key = colab_cache.fingerprint({'type': 'app', 'source': colab_cache.source_identity(source), 'system': system}) if cache else None
    native_key = colab_cache.fingerprint({'type': 'cuda', 'system': system, 'native': native_platform}) if cache and use_gpu else None
    distribution = work / 'app'
    data = work / 'data'
    data.mkdir(exist_ok=True)
    native = data / 'RuntimeStableDiffusionCpp/linux-cuda'
    if cache:
        colab_cache.restore(cache, app_key, distribution, ['Amuse.Web', 'AmuseHost.StableDiffusionCpp', 'AmuseHost.OnnxRuntime'])
        if use_gpu:
            colab_cache.restore(cache, native_key, native, ['libstable-diffusion.so'])
    need_app = not (distribution / 'Amuse.Web').is_file()
    need_native = use_gpu and not (native / 'libstable-diffusion.so').is_file()
    packages = {'ffmpeg':'ffmpeg', 'libgomp1':None, 'libglib2.0-0':None, 'libgl1':None}
    if need_app or need_native:
        packages.update({'build-essential':'g++', 'cmake':'cmake', 'git':'git', 'curl':'curl'})
    missing = []
    for package, command in packages.items():
        if command and shutil.which(command):
            continue
        result = subprocess.run(['dpkg-query', '-W', '-f=${Status}', package], capture_output=True, text=True)
        if result.returncode or result.stdout.strip() != 'install ok installed':
            missing.append(package)
    if missing:
        apt = ['apt-get'] if os.geteuid() == 0 else ['sudo', 'apt-get']
        execute([*apt, 'update', '-qq'])
        execute([*apt, 'install', '-y', '-qq', *missing])
    else:
        print('System dependencies already installed; skipping APT.', flush=True)
    dotnet = work / 'dotnet'
    if need_app and not (dotnet / 'dotnet').is_file():
        installer = work / 'dotnet-install.sh'
        urllib.request.urlretrieve('https://dot.net/v1/dotnet-install.sh', installer)
        execute(['bash', installer, '--channel', '10.0', '--install-dir', dotnet, '--no-path'])
    env = dict(os.environ, DOTNET_ROOT=str(dotnet), DOTNET_CLI_TELEMETRY_OPTOUT='1',
               DOTNET_NOLOGO='1', PATH=str(dotnet) + os.pathsep + os.environ['PATH'])
    if need_app:
        if distribution.exists():
            raise RuntimeError(f'Incomplete publish directory: {distribution}. Choose a new WORK directory.')
        execute(['bash', source / 'Scripts/publish-web-linux.sh', distribution], env=env)
        if cache:
            colab_cache.save(cache, app_key, distribution)
    if need_native:
        env['AMUSE_BUILD_JOBS'] = str(build_jobs or build_parallelism())
        print('CUDA build parallel jobs:', env['AMUSE_BUILD_JOBS'], flush=True)
        execute(['bash', source / 'Scripts/install-sd-cuda-linux.sh', data], env=env)
        if cache:
            colab_cache.save(cache, native_key, native)
    elif use_gpu:
        print('CUDA library already present; skipping CUDA compilation.', flush=True)
    return distribution, data


def https_origin(url):
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme != 'https' or not parsed.hostname or parsed.username or parsed.password or parsed.path not in ('', '/') or parsed.query or parsed.fragment:
        raise ValueError('Colab proxy/frame URL must be an HTTPS origin without a path or credentials.')
    return urllib.parse.urlunsplit(('https', parsed.netloc, '', '', ''))


class CloudflareTunnel:
    """Own a temporary Quick Tunnel and its log; never stop unrelated processes."""
    def __init__(self, work, port=8082):
        if not isinstance(port, int) or not 1 <= port <= 65535:
            raise ValueError('PORT must be an integer between 1 and 65535.')
        self.work, self.port = Path(work).resolve(), port
        self.process = self.log = self.url = None
        self.log_path = self.work / 'logs/cloudflared.log'

    def start(self, timeout=60):
        if self.process is not None:
            raise RuntimeError('Stop the previous tunnel before restarting.')
        binary = self.work / 'tools/cloudflared'
        if not binary.is_file():
            if platform.system() != 'Linux' or platform.machine() not in ('x86_64', 'amd64'):
                raise RuntimeError('Automatic cloudflared installation requires Linux x64.')
            binary.parent.mkdir(parents=True, exist_ok=True)
            temporary = binary.with_suffix('.download')
            try:
                urllib.request.urlretrieve('https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-amd64', temporary)
                temporary.chmod(0o755)
                temporary.replace(binary)
            finally:
                temporary.unlink(missing_ok=True)
        self.log_path.parent.mkdir(parents=True, exist_ok=True)
        self.log = self.log_path.open('wb', buffering=0)
        try:
            self.process = subprocess.Popen([str(binary), 'tunnel', '--no-autoupdate', '--url',
                                             f'http://127.0.0.1:{self.port}'],
                                            stdout=self.log, stderr=self.log)
            deadline = time.monotonic() + timeout
            while time.monotonic() < deadline:
                if self.process.poll() is not None:
                    raise RuntimeError(f'Cloudflare Tunnel exited. See {self.log_path}')
                match = re.search(r'https://[a-z0-9-]+\.trycloudflare\.com\b', self.log_path.read_text(errors='replace'))
                if match:
                    self.url = https_origin(match.group())
                    return self
                time.sleep(.25)
            raise TimeoutError(f'Cloudflare URL was not issued. See {self.log_path}')
        except BaseException:
            self.stop()
            raise

    def stop(self):
        if self.process is not None and self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()
        if self.log:
            self.log.close()
        self.process = self.log = self.url = None


class Server:
    def __init__(self, distribution, data, port=8082, *, proxy_url=None, frame_origin=None):
        if not isinstance(port, int) or not 1 <= port <= 65535:
            raise ValueError('PORT must be an integer between 1 and 65535.')
        self.distribution, self.data, self.port = Path(distribution).resolve(), Path(data).resolve(), port
        self.base = f'http://127.0.0.1:{port}'
        self.process = None
        self.token = None
        self.log = None
        self.proxy_url = https_origin(proxy_url) if proxy_url else None
        self.frame_origin = https_origin(frame_origin) if frame_origin else None
        if self.frame_origin and not self.proxy_url:
            raise ValueError('frame_origin requires proxy_url.')

    def start(self):
        with socket.socket() as check:
            try:
                check.bind(('127.0.0.1', self.port))
            except OSError as exc:
                raise RuntimeError(f'Port {self.port} is occupied. Stop your previous server or choose another PORT.') from exc
        self.data.mkdir(parents=True, exist_ok=True)
        command = [str(self.distribution / 'Amuse.Web'), '--port', str(self.port), '--data-dir', str(self.data)]
        if self.proxy_url:
            command += ['--colab-origin', self.proxy_url]
            if self.frame_origin:
                command += ['--frame-origin', self.frame_origin]
        self.log = (self.data / 'colab-server.log').open('wb', buffering=0)
        try:
            self.process = subprocess.Popen(command, cwd=self.distribution, stdout=self.log, stderr=self.log)
            for _ in range(300):
                if self.process.poll() is not None:
                    raise RuntimeError('Amuse exited.\n' + self.logs())
                if f'Now listening on: http://localhost:{self.port}' not in self.logs():
                    time.sleep(.1)
                    continue
                try:
                    self.token = self.request('/api/session')['token']
                    if self.process.poll() is not None:
                        raise RuntimeError('Amuse exited.\n' + self.logs())
                    print('Amuse ready:', self.request('/api/health'), flush=True)
                    return self
                except (OSError, urllib.error.URLError):
                    time.sleep(.1)
            raise TimeoutError('Server startup timed out.\n' + self.logs())
        except BaseException:
            self.stop()
            raise

    def request(self, path, data=None, *, method=None, raw=False):
        headers = {}
        if data is not None:
            headers['Content-Type'] = 'application/json'
            data = json.dumps(data).encode()
            method = method or 'POST'
        if method and method not in ('GET', 'HEAD'):
            headers['X-Amuse-Token'] = self.token or ''
        req = urllib.request.Request(self.base + path, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=30) as response:
                body = response.read()
                return body if raw else json.loads(body) if body else None
        except urllib.error.HTTPError as exc:
            with exc:
                raise RuntimeError(f'{exc.code}: {exc.read().decode(errors="replace")}') from exc

    def wait_job(self, job, timeout=1800):
        deadline, previous = time.monotonic() + timeout, None
        while time.monotonic() < deadline:
            job = self.request('/api/jobs/' + job['id'])
            current = (job['status'], job['message'], job['value'], job['maximum'])
            if current != previous:
                print(*current, flush=True)
                previous = current
            if job['status'] == 'completed':
                return job
            if job['status'] not in ('queued', 'running', 'canceling'):
                raise RuntimeError(job.get('error') or job['status'])
            time.sleep(1)
        raise TimeoutError(f"Job {job['id']} is still running; inspect /api/jobs or cancel it explicitly.")

    def initialize_environment(self, use_gpu=True):
        catalog = self.request('/api/catalog')
        name = 'linux-cuda' if use_gpu else 'linux-cpu'
        index = next(i for i, value in enumerate(catalog['environments']) if value['environment'] == name)
        return self.wait_job(self.request(f'/api/environments/{index}/create', {}))

    def logs(self):
        path = self.data / 'colab-server.log'
        if not path.exists():
            return ''
        with path.open('rb') as stream:
            stream.seek(max(0, path.stat().st_size - 12000))
            return stream.read().decode(errors='replace')

    def stop(self):
        if self.process and self.process.poll() is None:
            try:
                if self.token:
                    self.request('/api/server/stop', {}, method='POST')
                else:
                    self.process.terminate()
                self.process.wait(timeout=30)
            except Exception:
                self.process.terminate()
                try:
                    self.process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    self.process.wait()
        if self.log:
            self.log.close()
        self.process = None


def sample_image(server, prompt, *, model_id=550, use_gpu=True, seed=42):
    catalog = server.request('/api/catalog')
    device = next((x for x in catalog['devices'] if (x['vendor'] == 'Nvidia') == use_gpu), None)
    if device is None:
        raise RuntimeError('Requested execution device was not detected.')
    model = next(x for x in catalog['models'] if x['kind'] == 'diffusion' and x['id'] == model_id)
    if model['status'] != 'Installed':
        print('Downloading model and its components; this may use several GB.', flush=True)
        server.wait_job(server.request(f'/api/models/diffusion/{model_id}/download', {}), timeout=7200)
    job = server.request('/api/generate', {
        'modelKind': 'diffusion', 'modelId': model_id, 'view': 'TextToImage', 'deviceId': device['id'],
        'quality': 'Standard', 'memory': 'Low', 'count': 1,
        'options': {'prompt': prompt, 'width': 256, 'height': 256, 'steps': 4, 'seed': seed, 'guidanceScale': 1}
    })
    return server.wait_job(job)
