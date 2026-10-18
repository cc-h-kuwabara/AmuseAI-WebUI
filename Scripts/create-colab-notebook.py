"""Generate the checked-in Colab notebook with no saved outputs or credentials."""
from pathlib import Path
import hashlib
import json
import textwrap

ROOT = Path(__file__).resolve().parents[1]
cells = []

def cell(kind, source):
    source = textwrap.dedent(source).strip() + '\n'
    value = {'cell_type': kind, 'metadata': {}, 'source': source.splitlines(keepends=True),
             'id': hashlib.sha256(source.encode()).hexdigest()[:12]}
    if kind == 'code':
        value.update(execution_count=None, outputs=[])
    cells.append(value)

cell('markdown', '''
# Amuse Web — Google Colab

NVIDIA GPUでLinux Web版を動かすノートブックです。「ランタイム → ランタイムのタイプを変更」でGPUを選び、上から順に実行します。初回は.NETとCUDAライブラリーのビルドに時間がかかります。

**用意するファイル:** このノートブックと一緒に配布する `Amuse_Colab_Source.zip`。まだGitHubへ公開していない変更も、このZIPから実行できます。Pythonのグローバルパッケージは変更しません。

Web UIはColab公式ポートプロキシでノートブック内に表示します。公開トンネルや常時起動・切断回避処理は使いません。[Colab公式FAQ](https://research.google.com/colaboratory/faq.html)では、無料枠・コンピュートユニット残高なしでWeb UIを主に使う生成処理が制限されています。その場合は `USE_WEB_UI=False` とし、下の生成セルから実行してください。有料枠でもランタイムの寿命・利用可能GPUは保証されません。

モデル・結果は通常ランタイム終了時に失われます。必要ならDriveへモデルを保存し、最後のセルで結果をダウンロードします。Colab上のCUDA実推論は別途実機確認が必要です。
''')
cell('code', '''
from pathlib import Path
WORK = Path('/content/amuse')
PORT = 8080
USE_GPU = True
USE_WEB_UI = True
MOUNT_DRIVE = False
MODEL_DIRECTORY = ''  # 例: /content/drive/MyDrive/Amuse/Models
SOURCE_MODE = 'upload'  # upload または git
SOURCE_ARCHIVE = ''  # 空ならアップロード画面。Drive上のZIPパスも指定可能
GIT_URL = 'https://github.com/cc-h-kuwabara/AmuseAI-WebUI.git'
GIT_REF = 'webui'  # colab_runtime.py を含む版が公開されている場合のみ使用
SOURCE = WORK / 'source'
DATA = WORK / 'data'
''')
cell('markdown', '## 1. ソースを準備する\nDriveのマウントは任意です。アップロードするZIPには実行するソースコードが含まれるため、自分で作成した配布ZIPを使ってください。')
cell('code', '''
import os, sys, subprocess, zipfile
if MOUNT_DRIVE:
    from google.colab import drive
    drive.mount('/content/drive')
if MODEL_DIRECTORY.startswith('/content/drive/') and not MOUNT_DRIVE:
    raise ValueError('Driveのモデル保存先を使う場合は MOUNT_DRIVE=True にしてください。')
WORK.mkdir(parents=True, exist_ok=True)
if SOURCE.exists():
    if not (SOURCE / 'Scripts/colab_runtime.py').is_file():
        raise RuntimeError('不完全なソース保存先です。WORKを別のフォルダーに変更してください。')
    print('既存ソースを再利用:', SOURCE)
elif SOURCE_MODE == 'git':
    subprocess.run(['git', 'clone', '--depth', '1', '--branch', GIT_REF, GIT_URL, str(SOURCE)], check=True)
    if not (SOURCE / 'Scripts/colab_runtime.py').is_file():
        raise RuntimeError('このGit版にはColab対応がありません。配布ZIPを使用してください。')
elif SOURCE_MODE == 'upload':
    if SOURCE_ARCHIVE:
        archive = Path(SOURCE_ARCHIVE)
    else:
        from google.colab import files
        uploaded = files.upload()
        if len(uploaded) != 1:
            raise ValueError('Amuse_Colab_Source.zip を1つ選んでください。')
        archive = Path(next(iter(uploaded))).resolve()
        del uploaded
    with zipfile.ZipFile(archive) as bundle:
        if 'Scripts/colab_runtime.py' not in bundle.namelist():
            raise ValueError('配布された Amuse_Colab_Source.zip を選んでください。')
        for entry in bundle.infolist():
            target = (SOURCE / entry.filename).resolve()
            if not target.is_relative_to(SOURCE.resolve()) or (entry.external_attr >> 16) & 0o170000 == 0o120000:
                raise ValueError('安全でないZIPパス: ' + entry.filename)
        bundle.extractall(SOURCE)
else:
    raise ValueError('SOURCE_MODE は upload または git です。')
sys.path.insert(0, str(SOURCE / 'Scripts'))
import colab_runtime as amuse
print('Source ready:', SOURCE)
''')
cell('markdown', '## 2. ビルドする\n依存ライブラリー、プロジェクト専用.NET SDK、Web版、AIホストを準備します。GPUを使う場合は固定版StableDiffusion.cppのCUDA共有ライブラリーも構築します。モデルのダウンロードはまだ行いません。')
cell('code', '''
distribution, DATA = amuse.prepare(SOURCE, WORK, use_gpu=USE_GPU)
print('配布版:', distribution)
print('データ:', DATA)
''')
cell('markdown', '## 3. 起動・表示\nポートは設定セルのPORTで変更できます。同じセルの再実行時は、このノートブックで起動したサーバーを停止してから再起動します。別のプロセスは停止しません。')
cell('code', '''
if 'amuse_server' in globals():
    amuse_server.stop()
proxy_url = frame_origin = None
if USE_WEB_UI:
    import json
    from google.colab import output
    proxy_url = output.eval_js(f'google.colab.kernel.proxyPort({int(PORT)})')
    frame_origin = output.eval_js('window.location.origin')
    # 現在の出力セルだけを埋め込み元として許可する
    amuse.https_origin(proxy_url)
    amuse.https_origin(frame_origin)
amuse_server = amuse.Server(distribution, DATA, PORT, proxy_url=proxy_url, frame_origin=frame_origin).start()
amuse_server.initialize_environment(use_gpu=USE_GPU)
if MODEL_DIRECTORY:
    general = amuse_server.request('/api/config/general')
    general['directoryModel'] = MODEL_DIRECTORY
    amuse_server.wait_job(amuse_server.request('/api/config/general', general, method='PUT'))
catalog = amuse_server.request('/api/catalog')
print('実行デバイス:', [(d['id'], d['name']) for d in catalog['devices']])
if USE_WEB_UI:
    output.serve_kernel_port_as_iframe(PORT, height=1000)
else:
    print('ノートブックAPIモードです。次の生成セルを使用してください。')
''')
cell('markdown', '''
## 4. セルから画像を生成する（任意）

`RUN_SAMPLE=True` にすると、FLUX.2 Klein 4B（ID 550）と必要コンポーネントを導入し、256×256・4ステップで画像を生成します。初回ダウンロードは数GBになるため、ディスクと利用時間を確保してください。Web UIから同じモデルを導入済みなら再ダウンロードはしません。

モデルにトークンが必要な場合はWeb UIの設定から入力してください。トークンをソースや出力セルに記録しないでください。
''')
cell('code', '''
RUN_SAMPLE = False
PROMPT = 'A small blue ceramic teapot on a wooden desk, soft daylight, original illustration'
if RUN_SAMPLE:
    from IPython.display import display, Image
    result = amuse.sample_image(amuse_server, PROMPT, use_gpu=USE_GPU)
    media = {f['id']: f for f in amuse_server.request('/api/files')}
    for ident in result['files']:
        if media[ident]['contentType'].startswith('image/'):
            display(Image(data=amuse_server.request('/api/files/' + ident, raw=True)))
''')
cell('markdown', '## 5. 状態確認・結果の保存（任意）\n結果ZIPには画像・入力ファイル・生成レシピが含まれます。認証トークンを含むSettings.jsonは収録しません。')
cell('code', '''
print([(j['id'], j['status'], j['message']) for j in amuse_server.request('/api/jobs')[:10]])
DOWNLOAD_RESULTS = False
if DOWNLOAD_RESULTS:
    import shutil
    from google.colab import files
    archive = shutil.make_archive(str(WORK / 'Amuse_Results'), 'zip', DATA / 'Files')
    files.download(archive)
# 障害調査時だけ実行: print(amuse_server.logs())
''')
cell('markdown', '## 6. 停止\n処理が終わったらこのセルで停止し、不要ならColabランタイムも切断してください。')
cell('code', '''
if 'amuse_server' in globals():
    amuse_server.stop()
    print('Amuseを停止しました。')
''')
notebook = {'nbformat': 4, 'nbformat_minor': 5, 'metadata': {
    'colab': {'name': 'Amuse_Web_Colab.ipynb', 'provenance': []},
    'kernelspec': {'name': 'python3', 'display_name': 'Python 3'}, 'accelerator': 'GPU'}, 'cells': cells}
path = ROOT / 'Notebooks/Amuse_Web_Colab.ipynb'
path.parent.mkdir(exist_ok=True)
path.write_bytes((json.dumps(notebook, ensure_ascii=False, indent=2) + '\n').encode())
print(path)
