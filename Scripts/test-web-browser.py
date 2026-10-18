"""Browser checks through Chrome DevTools MCP. Run against a disposable Web data directory."""
import argparse
import json
from pathlib import Path
import sys
import time

from chrome_mcp_client import ChromeMcp

sys.stdout.reconfigure(encoding='utf-8')
sys.stderr.reconfigure(encoding='utf-8')
ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--url', default='http://localhost:18080')
parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/browser-qa')
parser.add_argument('--skip-inference', action='store_true')
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
checks = []
issues = []


def passed(name, detail=None):
    checks.append({'name': name, 'detail': detail})
    print('PASS:', name, detail or '', flush=True)


def wait(browser, condition, timeout=20):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if browser.evaluate('() => (' + condition + ')'):
            return
        time.sleep(.2)
    raise AssertionError('Browser condition timed out: ' + condition)


with ChromeMcp(ROOT) as browser:
    browser.open(args.url)
    try:
        wait(browser, 'document.getElementById("model").options.length > 0 && document.getElementById("device").options.length > 0')
        snapshot = browser.snapshot()
        (args.output / 'initial-snapshot.txt').write_text(snapshot, encoding='utf-8')
        browser.call('resize_page', width=1440, height=1000)
        browser.screenshot(args.output / 'initial-desktop.png')
        passed('Initial rendering, GPU and model selection', browser.evaluate('() => ({models:document.getElementById("model").options.length,gpu:document.getElementById("device").selectedOptions[0].textContent})'))

        navigation = {'process': '↗ メディア加工', 'editor': '▧ 画像エディター', 'video': '▹ 動画エディター',
                      'gallery': '▦ ギャラリー', 'jobs': '≡ ジョブ・履歴', 'models': '◇ モデル',
                      'environments': '⌘ 実行環境', 'settings': '⚙ 設定', 'generate': '✧ 生成スタジオ'}

        def navigate(page):
            browser.click_link(args.url.rstrip('/') + '/#' + page)
            wait(browser, 'document.querySelector(".page:not([hidden])").id === ' + json.dumps(page))

        for page in navigation:
            navigate(page)
        passed('All nine navigation routes')

        workflow_values = browser.evaluate('() => Array.from(document.getElementById("workflow").options).map(x=>x.value)')
        workflow_counts = {}
        for workflow in workflow_values:
            browser.fill('combobox "ワークフロー"', workflow)
            workflow_counts[workflow] = browser.evaluate('() => document.getElementById("model").options.length')
        assert all(workflow_counts.values()), workflow_counts
        browser.fill('combobox "ワークフロー"', 'TextToImage')
        passed('All fifteen workflow selectors', workflow_counts)

        navigate('models')
        browser.fill('searchbox "検索"', 'FLUX.2 Klein 4B')
        wait(browser, 'document.querySelectorAll("#model-list .model-card").length > 0 && Array.from(document.querySelectorAll("#model-list h3")).every(x=>x.textContent.includes("FLUX.2 Klein 4B"))')
        browser.screenshot(args.output / 'models-filtered.png')
        passed('Model search filtering')

        navigate('settings')
        wait(browser, 'document.querySelector("[data-setting=historyItems]") !== null')
        original_history = browser.evaluate('() => document.querySelector("[data-setting=historyItems]").value')
        browser.fill('spinbutton "履歴の最大表示数"', '43' if original_history != '43' else '44')
        browser.click('button "設定を保存"')
        wait(browser, 'document.querySelector("#job-list .job .state.completed") !== null')
        navigate('generate'); navigate('settings')
        wait(browser, 'document.querySelector("[data-setting=historyItems]")?.value === ' + json.dumps('43' if original_history != '43' else '44'))
        browser.fill('spinbutton "履歴の最大表示数"', original_history)
        browser.click('button "設定を保存"')
        wait(browser, 'Array.from(document.querySelectorAll("#job-list .job .state")).filter(x=>x.classList.contains("completed")).length >= 2')
        browser.fill('combobox "設定項目"', 'tokens')
        wait(browser, 'document.querySelectorAll("[data-token]").length > 0')
        assert browser.evaluate('() => Array.from(document.querySelectorAll("[data-token]")).every(x=>x.type==="password" && x.value==="")')
        passed('Settings persistence, restoration and masked credentials')

        navigate('gallery')
        before_upload = browser.evaluate('() => document.querySelectorAll("#gallery-grid .file-card").length')
        browser.call('upload_file', uid=browser.uid('button "ファイルを追加"'), filePaths=[str(ROOT / 'Amuse.App/Images/Icon.png')])
        wait(browser, f'document.querySelectorAll("#gallery-grid .file-card").length === {before_upload + 1}')
        passed('File upload through the browser file input')

        navigate('editor')
        browser.click('button "黒いマスクで開始"')
        browser.click('Canvas "描画キャンバス"')
        assert browser.evaluate('() => document.getElementById("canvas").getContext("2d").getImageData(384,384,1,1).data[0] > 0')
        browser.click('button "元に戻す"')
        assert browser.evaluate('() => document.getElementById("canvas").getContext("2d").getImageData(384,384,1,1).data[0] === 0')
        browser.fill('spinbutton "幅"', '64'); browser.fill('spinbutton "高さ"', '64')
        browser.click('button "サイズを適用"')
        assert browser.evaluate('() => document.getElementById("canvas").width===64 && document.getElementById("canvas").height===64')
        browser.click('button "PNGを保存"')
        wait(browser, f'document.querySelectorAll("#gallery-grid .file-card").length === {before_upload + 2}')
        passed('Canvas mask, pointer drawing, Undo, resizing and PNG saving')

        navigate('gallery')
        deleted_name = browser.evaluate('() => document.querySelector("#gallery-grid .file-card p").textContent')
        browser.click('button "ごみ箱へ"')
        wait(browser, f'document.querySelectorAll("#gallery-grid .file-card").length === {before_upload + 1}')
        browser.click('button "ごみ箱・復元"')
        wait(browser, 'document.querySelector("#trash-list .model-card") !== null')
        browser.click('button "復元"')
        wait(browser, f'document.querySelectorAll("#gallery-grid .file-card").length === {before_upload + 2}')
        passed('Gallery soft-delete and restore', deleted_name)

        navigate('video')
        source = browser.evaluate('() => Array.from(document.getElementById("video-source").options).find(x=>x.textContent==="Icon.png").value')
        browser.fill('combobox "ギャラリーから追加"', source)
        browser.click('button "クリップを追加"')
        browser.fill('spinbutton "長さ（秒）"', '.5')
        browser.fill('spinbutton "幅"', '64'); browser.fill('spinbutton "高さ"', '64')
        browser.fill('spinbutton "FPS"', '12')
        browser.click('button "動画を書き出す"')
        wait(browser, 'Array.from(document.querySelectorAll("#job-list .job")).some(x=>x.querySelector(".job-title").textContent==="VideoCompose" && x.querySelector(".state.completed"))', timeout=45)
        navigate('generate')
        wait(browser, 'document.querySelector("#result video") !== null')
        playback = browser.evaluate('async () => {const v=document.querySelector("#result video"); await v.play(); await new Promise(resolve=>v.addEventListener("ended",resolve,{once:true})); return {width:v.videoWidth,height:v.videoHeight,time:v.currentTime,duration:v.duration,error:v.error};}')
        assert playback['width'] == 64 and playback['height'] == 64 and not playback['error'], playback
        passed('Video editor export and actual Chrome video playback', playback)

        browser.fill('combobox "モデル"', 'diffusion:550')
        browser.fill('textbox "プロンプト"', 'A small blue ceramic teapot on a wooden desk, soft daylight, original illustration')
        browser.fill('spinbutton "幅"', '256'); browser.fill('spinbutton "高さ"', '256')
        browser.fill('spinbutton "ステップ"', '4'); browser.fill('spinbutton "Seed"', '42')
        browser.fill('spinbutton "CFG"', '1')
        browser.click('button "↻ 更新"')
        preserved = browser.evaluate('() => ({width:document.getElementById("width").value,steps:document.getElementById("steps").value})')
        wait(browser, 'document.getElementById("notice").textContent.includes("更新しました")')
        preserved = browser.evaluate('() => ({width:document.getElementById("width").value,steps:document.getElementById("steps").value})')
        if preserved != {'width': '256', 'steps': '4'}:
            issues.append({'name': 'Refresh resets edited generation settings', 'observed': preserved})
            print('ISSUE: refresh resets generation settings', preserved, flush=True)
        else:
            passed('Refresh preserves generation settings')
        browser.fill('spinbutton "幅"', '256'); browser.fill('spinbutton "高さ"', '256')
        browser.fill('spinbutton "ステップ"', '4')
        browser.click('DisclosureTriangle "詳細パラメーター"')
        before_invalid = browser.evaluate('() => document.querySelectorAll("#job-list .job").length')
        browser.fill('textbox "詳細パラメーター"', '{')
        browser.click('button "✧ 生成をキューに追加"')
        wait(browser, 'document.getElementById("notice").classList.contains("error")')
        assert browser.evaluate('() => document.querySelectorAll("#job-list .job").length') == before_invalid
        browser.fill('textbox "詳細パラメーター"', '{}')
        browser.click('DisclosureTriangle "詳細パラメーター"')
        passed('Invalid JSON is reported without starting a generation job')

        if not args.skip_inference:
            browser.click('button "✧ 生成をキューに追加"')
            wait(browser, 'document.querySelector("#current-jobs .state.running") !== null', timeout=20)
            generation_id = browser.evaluate('() => document.querySelector("#current-jobs .state.running").closest(".job").dataset.jobId')
            browser.call('navigate_page', type='reload')
            wait(browser, 'document.querySelector("#current-jobs .state.running") !== null || document.querySelector("#result img") !== null', timeout=20)
            wait(browser, 'document.getElementById("model").value==="diffusion:550" && document.getElementById("width").value==="256"', timeout=20)
            wait(browser, 'document.querySelector("#job-list .job")?.dataset.jobId===' + json.dumps(generation_id) + ' && !!document.querySelector("#job-list .job").querySelector(".state.completed") && document.querySelector("#result img")?.complete && document.querySelector("#result img").naturalWidth===256 && document.getElementById("result-status").textContent==="COMPLETED"', timeout=180)
            browser.screenshot(args.output / 'generated-image.png')
            passed('Real image generation, reconnect after reload and result display')

        mobile_results = {}
        browser.call('emulate', viewport='390x844x1,mobile,touch')
        wait(browser, 'document.getElementById("model").options.length > 0 && document.getElementById("device").options.length > 0')
        for page in navigation:
            navigate(page)
            mobile_results[page] = browser.evaluate('() => ({viewport:innerWidth,document:document.documentElement.scrollWidth})')
        mobile_issues = {p: d for p, d in mobile_results.items() if d['document'] > 390}
        if mobile_issues:
            issues.append({'name': 'Mobile horizontal overflow', 'observed': mobile_issues})
            print('ISSUE: mobile horizontal overflow', mobile_issues, flush=True)
        else:
            passed('All nine pages at a 390px mobile viewport')
        browser.screenshot(args.output / 'mobile.png')
        console = browser.call('list_console_messages', types=['error', 'warn'], includeStackTraces=True)
        (args.output / 'console.txt').write_text(console, encoding='utf-8')
        print('Console:', console, flush=True)
        if '<no console messages found>' in console:
            passed('No browser console errors or warnings')
        else:
            issues.append({'name': 'Browser console messages', 'observed': console})
    except Exception:
        (args.output / 'failure-snapshot.txt').write_text(browser.snapshot(), encoding='utf-8')
        browser.screenshot(args.output / 'failure.png')
        print(browser.call('list_console_messages', types=['error', 'warn'], includeStackTraces=True), flush=True)
        raise
    finally:
        (args.output / 'report.json').write_text(json.dumps({'checks': checks, 'issues': issues}, ensure_ascii=False, indent=2), encoding='utf-8')
print(f'{len(checks)} browser checks passed; {len(issues)} issues.', flush=True)
