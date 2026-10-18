"""Cancel a real model load from the browser, then generate again to check recovery.

Requires the installed FLUX.2 Klein 4B model (ID 550) and a disposable running Web instance.
"""
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
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
checks = []


def wait(browser, condition, timeout=30, job_id=None):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if browser.evaluate('() => (' + condition + ')'):
            return
        if job_id:
            failure = browser.evaluate('() => { const j=Array.from(document.querySelectorAll("#job-list .job")).find(x=>x.dataset.jobId===' + json.dumps(job_id) + '); return j?.querySelector(".state.failed") ? j.innerText : null; }')
            if failure:
                raise AssertionError(failure)
        time.sleep(.25)
    raise AssertionError('Browser condition timed out: ' + condition)


with ChromeMcp(ROOT) as browser:
    try:
        browser.open(args.url)
        wait(browser, 'document.getElementById("model").options.length>0')
        browser.fill('combobox "モデル"', 'diffusion:550')
        browser.fill('textbox "プロンプト"', 'A small blue ceramic teapot on a wooden desk, soft daylight, original illustration')
        browser.fill('spinbutton "幅"', '256'); browser.fill('spinbutton "高さ"', '256')
        browser.fill('spinbutton "ステップ"', '4'); browser.fill('spinbutton "Seed"', '43')
        browser.fill('spinbutton "CFG"', '1')
        browser.click('button "✧ 生成をキューに追加"')
        wait(browser, 'document.querySelector("#current-jobs .state.running")!==null')
        canceled = browser.evaluate('() => document.querySelector("#current-jobs .state.running").closest(".job").dataset.jobId')
        browser.click('button "キャンセル"')
        wait(browser, 'document.querySelector("#job-list .job")?.dataset.jobId===' + json.dumps(canceled) + ' && !!document.querySelector("#job-list .job").querySelector(".state.canceled")', timeout=40)
        checks.append({'name': 'Browser cancellation of a real model job', 'jobId': canceled})
        print('PASS: browser cancellation of a real model job', canceled, flush=True)

        browser.click('button "✧ 生成をキューに追加"')
        wait(browser, 'document.querySelector("#current-jobs .state.running")!==null')
        recovered = browser.evaluate('() => document.querySelector("#current-jobs .state.running").closest(".job").dataset.jobId')
        assert recovered != canceled
        wait(browser, 'document.querySelector("#job-list .job")?.dataset.jobId===' + json.dumps(recovered) + ' && !!document.querySelector("#job-list .job").querySelector(".state.completed") && document.querySelector("#result img")?.complete && document.querySelector("#result img").naturalWidth===256', timeout=180, job_id=recovered)
        checks.append({'name': 'Real image generation succeeds after cancellation', 'jobId': recovered})
        browser.screenshot(args.output / 'cancel-recovery.png')
        print('PASS: real image generation succeeds after cancellation', recovered, flush=True)
    except Exception:
        (args.output / 'cancellation-failure.txt').write_text(browser.snapshot(), encoding='utf-8')
        browser.screenshot(args.output / 'cancellation-failure.png')
        raise
    finally:
        (args.output / 'cancellation-report.json').write_text(json.dumps({'checks': checks}, ensure_ascii=False, indent=2), encoding='utf-8')
