"""Verified, versioned build cache. Never caches models, tokens or runtime data."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import uuid


def digest_file(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def fingerprint(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True).encode()).hexdigest()


def source_identity(source):
    source = Path(source)
    if (source / '.git').is_dir():
        head = subprocess.check_output(['git', '-C', str(source), 'rev-parse', 'HEAD']).strip()
        changes = subprocess.check_output(['git', '-C', str(source), 'diff', 'HEAD', '--binary'])
        return hashlib.sha256(head + changes).hexdigest()
    digest = hashlib.sha256()
    for path in sorted(source.rglob('*')):
        if not path.is_file() or {'bin', 'obj', '.git', 'artifacts', '__pycache__'} & set(path.relative_to(source).parts):
            continue
        if path.suffix.lower() in ('.cs', '.csproj', '.props', '.resx', '.json', '.js', '.css', '.html', '.py', '.sh'):
            digest.update(path.relative_to(source).as_posix().encode())
            digest.update(digest_file(path).encode())
    return digest.hexdigest()


def restore(cache, key, target, required):
    cache, target = Path(cache), Path(target)
    archive, manifest = cache / (key + '.tar.gz'), cache / (key + '.json')
    if target.exists():
        return False
    if not archive.is_file() or not manifest.is_file():
        return False
    try:
        meta = json.loads(manifest.read_text())
        if meta.get('key') != key or meta.get('sha256') != digest_file(archive):
            print('Build cache checksum mismatch; rebuilding.', flush=True)
            return False
        target.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='.amuse-restore-', dir=target.parent) as temporary:
            stage = Path(temporary) / 'payload'
            stage.mkdir()
            with tarfile.open(archive, 'r:gz') as bundle:
                bundle.extractall(stage, filter='data')
            if not all((stage / name).is_file() for name in required):
                print('Build cache is incomplete; rebuilding.', flush=True)
                return False
            stage.rename(target)
        print('Build cache restored:', target.name, flush=True)
        return True
    except (OSError, ValueError, tarfile.TarError) as exc:
        print(f'Build cache could not be restored ({type(exc).__name__}); rebuilding.', flush=True)
        return False


def save(cache, key, source):
    try:
        _save(cache, key, source)
    except OSError as exc:
        print(f'Build succeeded, but cache could not be saved ({type(exc).__name__}).', flush=True)


def _save(cache, key, source):
    cache, source = Path(cache), Path(source)
    ignored = {'Logs', 'Models', 'Temp', 'History', 'Files', 'Jobs', 'RuntimeHuggingFace', 'RuntimeStableDiffusionCpp'}
    cache.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='amuse-cache-') as temporary:
        archive = Path(temporary) / 'payload.tar.gz'
        with tarfile.open(archive, 'w:gz', compresslevel=1) as bundle:
            for path in sorted(source.rglob('*')):
                relative = path.relative_to(source)
                if relative.parts[0] in ignored or path.name == 'Settings.json' or path.suffix == '.pdb':
                    continue
                bundle.add(path, arcname=relative.as_posix(), recursive=False)
        meta = {'key': key, 'sha256': digest_file(archive), 'schema': 1}
        staged = cache / (key + '.tmp-' + uuid.uuid4().hex)
        try:
            shutil.copyfile(archive, staged)
            os.replace(staged, cache / (key + '.tar.gz'))
            staged.write_text(json.dumps(meta))
            os.replace(staged, cache / (key + '.json'))
        finally:
            staged.unlink(missing_ok=True)
    print('Build cache saved:', source.name, flush=True)
