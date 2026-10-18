"""Bundle build sources for Colab without credentials, model weights or build outputs."""
from pathlib import Path
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]
paths = {Path(p.decode('utf-8')) for p in subprocess.check_output(['git', 'ls-files', '-z'], cwd=ROOT).split(b'\0') if p}
paths.update(Path('Scripts') / name for name in ('colab_runtime.py', 'create-colab-notebook.py', 'package-colab-source.py', 'test-colab.py'))
paths.update((Path('Amuse.Web/BrowserAccess.cs'), Path('Notebooks/Amuse_Web_Colab.ipynb'), Path('Notebooks/README.md')))
output = ROOT / 'artifacts/Amuse_Colab_Source.zip'
output.parent.mkdir(exist_ok=True)
with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED) as bundle:
    for relative in sorted(paths):
        if any(p in ('bin', 'obj', 'artifacts', '.git', '__pycache__') for p in relative.parts):
            continue
        if relative.parts[0] == 'Assets' or relative.parts[:2] == ('Amuse.App', 'Fonts'):
            continue
        if (ROOT / relative).is_file():
            bundle.write(ROOT / relative, relative.as_posix())
print(output, output.stat().st_size, 'bytes')
