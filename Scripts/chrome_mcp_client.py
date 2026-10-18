"""Small stdio MCP client for browser QA through the configured Chrome DevTools MCP."""
import json
import os
from pathlib import Path
import queue
import re
import subprocess
import threading
import time
import tomllib


class ChromeMcp:
    def __init__(self, workspace):
        config = tomllib.loads((Path.home() / '.codex/config.toml').read_text(encoding='utf-8-sig'))
        server = config['mcp_servers']['chrome-devtools']
        self.process = subprocess.Popen(
            [server['command'], *server['args'], '--workspace', str(Path(workspace).resolve())],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            text=True, encoding='utf-8', creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        self.messages = queue.Queue()
        self.sequence = 0
        self.page = None
        threading.Thread(target=self._read, daemon=True).start()
        self.rpc('initialize', {'protocolVersion': '2024-11-05', 'capabilities': {},
                               'clientInfo': {'name': 'AmuseBrowserQA', 'version': '1.0'}})
        self.process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        self.process.stdin.flush()

    def _read(self):
        for line in self.process.stdout:
            try:
                self.messages.put(json.loads(line))
            except json.JSONDecodeError:
                pass

    def rpc(self, method, params):
        self.sequence += 1
        ident = self.sequence
        self.process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': ident, 'method': method, 'params': params}) + '\n')
        self.process.stdin.flush()
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            response = self.messages.get(timeout=max(.1, deadline - time.monotonic()))
            if response.get('id') == ident:
                if 'error' in response:
                    raise RuntimeError(response['error'])
                return response['result']
        raise TimeoutError(method)

    def call(self, name, **args):
        if self.page is not None and name != 'new_page':
            args.setdefault('pageId', self.page)
        result = self.rpc('tools/call', {'name': name, 'arguments': args})
        text = '\n'.join(c.get('text', '') for c in result.get('content', []))
        if result.get('isError'):
            raise RuntimeError(f'{name}: {text}')
        return text

    def open(self, url):
        pages = self.call('new_page', url=url)
        self.page = int(re.search(r'(?m)^(\d+): .*\[selected\]', pages).group(1))
        return self.page

    def evaluate(self, source):
        text = self.call('evaluate_script', function=source, waitForStableDom=False)
        match = re.search(r'```(?:json)?\s*([\s\S]*?)\s*```', text)
        if match and match.group(1).strip() == 'undefined':
            return None
        return json.loads(match.group(1)) if match else text

    def snapshot(self):
        return self.call('take_snapshot')

    def uid(self, description):
        snapshot = self.snapshot()
        line = next((line for line in snapshot.splitlines() if description in line and 'uid=' in line), None)
        if not line:
            raise AssertionError(f'Element not found: {description}\n{snapshot[:6000]}')
        return re.search(r'uid=([^\s]+)', line).group(1)

    def click(self, description):
        return self.call('click', uid=self.uid(description))

    def click_link(self, url):
        line = next(line for line in self.snapshot().splitlines() if ' link "' in line and f'url="{url}"' in line)
        return self.call('click', uid=re.search(r'uid=([^\s]+)', line).group(1))

    def fill(self, description, value):
        if description.startswith('combobox "'):
            label = description.split('"')[1]
            value = self.evaluate('() => { const label=' + json.dumps(label) + '; const value=' + json.dumps(str(value)) + ';'
                'const select=Array.from(document.querySelectorAll("select")).find(e=>Array.from(e.labels||[]).some(l=>l.childNodes[0]?.textContent.trim()===label));'
                'if(!select) throw new Error("Select not found: "+label);'
                'const option=Array.from(select.options).find(o=>o.value===value);'
                'if(!option) throw new Error("Option not found: "+value); return option.textContent; }')
        return self.call('fill', uid=self.uid(description), value=str(value))

    def screenshot(self, path):
        self.evaluate('() => { window.scrollTo(0,0); return true; }')
        return self.call('take_screenshot', filePath=str(Path(path).resolve()), fullPage=True)

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.terminate()
            self.process.wait(timeout=10)

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()
