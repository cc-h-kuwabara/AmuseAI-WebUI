"""Build-cache integrity and reuse checks; no GPU or network required."""
import io
import json
import os
import contextlib
import subprocess
import sys
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch

import colab_cache as cache
import colab_runtime as runtime


class CacheTests(unittest.TestCase):
    def test_cloudflare_url_and_owned_process_cleanup(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / 'tools').mkdir()
            (root / 'tools/cloudflared').touch()
            tunnel = runtime.CloudflareTunnel(root, 8082)
            with patch.object(runtime.subprocess, 'Popen') as spawn:
                spawn.return_value.poll.return_value = None

                def start(*args, **kwargs):
                    kwargs['stdout'].write(b'INF https://test-tunnel.trycloudflare.com\n')
                    return spawn.return_value

                spawn.side_effect = start
                tunnel.start(timeout=1)
                self.assertEqual(tunnel.url, 'https://test-tunnel.trycloudflare.com')
                self.assertIn('http://127.0.0.1:8082', spawn.call_args.args[0])
                tunnel.stop()
                spawn.return_value.terminate.assert_called_once()
                self.assertIsNone(tunnel.process)
                self.assertIsNone(tunnel.url)

    def test_cloudflare_timeout_stops_process(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / 'tools').mkdir()
            (root / 'tools/cloudflared').touch()
            tunnel = runtime.CloudflareTunnel(root)
            with patch.object(runtime.subprocess, 'Popen') as spawn:
                spawn.return_value.poll.return_value = None
                with self.assertRaises(TimeoutError):
                    tunnel.start(timeout=0)
                spawn.return_value.terminate.assert_called_once()
                self.assertIsNone(tunnel.log)

    def test_build_output_goes_to_log_and_failure_is_preserved(self):
        with tempfile.TemporaryDirectory() as tmp:
            log = Path(tmp) / 'logs/build.log'
            console = io.StringIO()
            code = "import sys; print('stdout-ready'); print('stderr-ready',file=sys.stderr); sys.exit(3)"
            with contextlib.redirect_stdout(console):
                with self.assertRaises(subprocess.CalledProcessError) as failure:
                    runtime.run([sys.executable, '-u', '-c', code], log_file=log)
            self.assertEqual(failure.exception.returncode, 3)
            content = log.read_text()
            self.assertIn('stdout-ready', content)
            self.assertIn('stderr-ready', content)
            self.assertIn('Exit code: 3', content)
            self.assertNotIn('stdout-ready', console.getvalue())
            self.assertNotIn('stderr-ready', console.getvalue())
            with contextlib.redirect_stdout(console):
                runtime.run([sys.executable, '-c', "print('next-run')"], log_file=log)
            self.assertIn('stdout-ready', log.read_text())
            self.assertIn('next-run', log.read_text())

    def test_roundtrip_excludes_runtime_data_and_preserves_mode(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source = root / 'source'
            source.mkdir()
            exe = source / 'Amuse.Web'
            exe.write_bytes(b'executable')
            exe.chmod(0o755)
            (source / 'Settings.json').write_text('secret')
            (source / 'Models').mkdir()
            (source / 'Models/weight').write_text('model')
            cache.save(root / 'cache', 'key', source)
            target = root / 'restored'
            self.assertTrue(cache.restore(root / 'cache', 'key', target, ['Amuse.Web']))
            self.assertEqual((target / 'Amuse.Web').read_bytes(), b'executable')
            self.assertFalse((target / 'Settings.json').exists())
            self.assertFalse((target / 'Models').exists())
            if os.name != 'nt':
                self.assertTrue(os.access(target / 'Amuse.Web', os.X_OK))
            self.assertFalse(cache.restore(root / 'cache', 'key', target, ['Amuse.Web']))
            (root / 'cache/key.tar.gz').write_bytes(b'corrupt')
            self.assertFalse(cache.restore(root / 'cache', 'key', root / 'bad', ['Amuse.Web']))

    def test_unsafe_archive_is_rejected_even_with_valid_checksum(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            archive = root / 'key.tar.gz'
            with tarfile.open(archive, 'w:gz') as bundle:
                item = tarfile.TarInfo('../escaped')
                item.size = 1
                bundle.addfile(item, io.BytesIO(b'x'))
            (root / 'key.json').write_text(json.dumps({'key': 'key', 'sha256': cache.digest_file(archive)}))
            self.assertFalse(cache.restore(root, 'key', root / 'target', ['escaped']))
            self.assertFalse((root / 'escaped').exists())

    def test_warm_prepare_skips_installs_and_builds(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / 'app').mkdir()
            (root / 'app/Amuse.Web').touch()
            with patch.object(runtime.platform, 'system', return_value='Linux'), \
                 patch.object(runtime.platform, 'machine', return_value='x86_64'), \
                 patch.object(runtime.platform, 'freedesktop_os_release', return_value={'VERSION_ID': '22.04'}), \
                 patch.object(runtime.shutil, 'which', return_value='/usr/bin/tool'), \
                 patch.object(runtime.subprocess, 'run') as process, \
                 patch.object(runtime, 'run') as run:
                process.return_value.returncode = 0
                process.return_value.stdout = 'install ok installed'
                distribution, _ = runtime.prepare(root, root, use_gpu=False)
                self.assertEqual(distribution, root / 'app')
                run.assert_not_called()

    def test_invalid_parallelism_fails_before_build(self):
        with self.assertRaises(ValueError):
            runtime.prepare('.', '.', build_jobs=-1)


if __name__ == '__main__':
    unittest.main()
