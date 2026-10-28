"""Live, owned transports for the embedded s&box chat (no workspace actions)."""
import json
import os
import selectors
import subprocess
import tempfile
import threading
import time
from pathlib import Path


def emit(value):
    print(json.dumps(value), flush=True)


def text_delta(text):
    if text:
        emit({'type': 'stream_event', 'event': {'type': 'content_block_delta',
             'delta': {'type': 'text_delta', 'text': text}}})


def thinking():
    emit({'type': 'stream_event', 'event': {'type': 'content_block_delta',
         'delta': {'type': 'thinking_delta'}}})


class Child:
    """Drain stderr concurrently; never leave a private server running on exit."""
    def __init__(self, args, **kwargs):
        self.proc = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=subprocess.PIPE, **kwargs)
        self.diagnostics = bytearray()
        def drain():
            while chunk := self.proc.stderr.read(4096):
                self.diagnostics.extend(chunk)
                del self.diagnostics[:-4000]
        self.thread = threading.Thread(target=drain, daemon=True)
        self.thread.start()

    def send(self, value):
        self.proc.stdin.write((json.dumps(value) + '\n').encode())
        self.proc.stdin.flush()

    def events(self):
        selector = selectors.DefaultSelector()
        selector.register(self.proc.stdout, selectors.EVENT_READ)
        # Silence, not a clock from the start. A model that keeps sending
        # events can run until its turn completes. 180s with no bytes is a hang.
        deadline = time.monotonic() + 180
        pending = b''
        try:
            while True:
                if time.monotonic() >= deadline:
                    raise RuntimeError('Provider went quiet for 180 seconds')
                if not selector.select(.1):
                    continue
                chunk = os.read(self.proc.stdout.fileno(), 65536)
                if not chunk:
                    break
                deadline = time.monotonic() + 180
                pending += chunk
                if len(pending) > 1048576:
                    raise RuntimeError('Provider event exceeded the output limit')
                while b'\n' in pending:
                    line, pending = pending.split(b'\n', 1)
                    try:
                        event = json.loads(line)
                    except ValueError:
                        continue
                    if isinstance(event, dict):
                        yield event
            self.thread.join(timeout=.2)
            raise RuntimeError('Provider stream ended before completion: ' +
                               self.diagnostics.decode(errors='replace')[-600:])
        finally:
            selector.close()

    def close(self):
        try:
            self.proc.stdin.close()
        except (BrokenPipeError, OSError):
            pass
        if self.proc.poll() is None:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=2)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait(timeout=2)
        self.thread.join(timeout=.2)
        self.proc.stdout.close()
        self.proc.stderr.close()


def codex(exe, model, prompt, effort='', images=()):
    # Keep the account/provider configuration, but omit desktop workspace
    # instructions, skills and action tools from this script-only integration.
    config = {'project_doc_max_bytes': 0, 'skills.include_instructions': False,
              'features.shell_tool': False, 'features.apps': False,
              'features.plugins': False, 'features.memories': False,
              'agents.enabled': False, 'web_search': 'disabled',
              'allow_managed_hooks_only': True, 'mcp_servers': {}}
    args = [exe, 'app-server', '--stdio']
    for key, value in config.items():
        args += ['-c', key + '=' + json.dumps(value)]
    workspace = tempfile.TemporaryDirectory(prefix='geppetto-codex-')
    child = Child(args, cwd=workspace.name)
    reply = []
    item_text = {}
    try:
        child.send({'id': 0, 'method': 'initialize', 'params': {
            'clientInfo': {'name': 'geppetto_sbox', 'version': '1.0'}}})
        for event in child.events():
            if event.get('error'):
                raise RuntimeError('Codex: ' + event['error'].get('message', 'request failed'))
            if event.get('id') == 0 and 'result' in event:
                child.send({'method': 'initialized', 'params': {}})
                params = {'ephemeral': True, 'cwd': workspace.name,
                          'sandbox': 'read-only', 'approvalPolicy': 'never',
                          'baseInstructions': 'You are the embedded Geppetto modeller. Follow the supplied native API and return effigy script. Do not use tools or research; the caller executes your script live.',
                          'developerInstructions': 'Return an executable first batch promptly. Do not inspect files or follow desktop research/approval workflows.'}
                if model:
                    params['model'] = model
                child.send({'id': 1, 'method': 'thread/start', 'params': params})
            elif event.get('id') == 1 and 'result' in event:
                thread_id = event['result']['thread']['id']
                params = {'threadId': thread_id, 'input': [{'type': 'text', 'text': prompt}] + [{'type':'localImage','path':str(Path(p).resolve())} for p in images]}
                if effort:
                    params['effort'] = effort
                child.send({'id': 2, 'method': 'turn/start', 'params': params})
            method, params = event.get('method', ''), event.get('params', {})
            if method == 'item/agentMessage/delta':
                delta = params.get('delta', '')
                key = params.get('itemId', '')
                item_text[key] = item_text.get(key, '') + delta
                reply.append(delta)
                text_delta(delta)
            elif method.startswith('item/reasoning/'):
                thinking()
            elif method == 'item/completed' and params.get('item', {}).get('type') == 'agentMessage':
                item = params['item']
                if not item_text.get(item.get('id', '')):
                    text = item.get('text', '')
                    reply.append(text)
                    text_delta(text)
            elif method == 'item/started':
                kind = params.get('item', {}).get('type')
                if kind not in ('userMessage', 'agentMessage', 'reasoning', None):
                    raise RuntimeError('Codex attempted ' + str(kind) + '; native chat only accepts modelling replies')
            elif method == 'turn/completed':
                turn = params.get('turn', {})
                if turn.get('status') != 'completed':
                    raise RuntimeError('Codex: ' + str(turn.get('error') or turn.get('status')))
                if not ''.join(reply).strip():
                    raise RuntimeError('Codex returned no text')
                return ''.join(reply)
            elif 'id' in event and method:
                # Never leave an approval/tool request waiting silently.
                raise RuntimeError('Codex requested an unsupported interaction: ' + method)
    finally:
        child.close()
        workspace.cleanup()


def opencode(exe, model, prompt, images=()):
    # ACP provides token deltas; `run --format json` only emits whole text parts.
    # ACP owns a private server and shuts it down when stdin closes.
    with tempfile.TemporaryDirectory(prefix='geppetto-opencode-') as directory:
        config = {'default_agent': 'geppetto-native', 'warming': False, 'snapshots': False,
                  'agents': {'geppetto-native': {'mode': 'primary', 'steps': 2,
                    'system': 'You are the embedded Geppetto modeller. Generate executable effigy script using the supplied current API. No research, file inspection, tools or planning gates. The host executes your script live. Return a small first batch promptly.',
                    'permissions': [{'action': '*', 'resource': '*', 'effect': 'deny'}]}}}
        if model:
            # Root model does not retain variants; select effort through ACP below.
            config['model'] = model.split('#', 1)[0]
        (Path(directory) / 'opencode.json').write_text(json.dumps(config), encoding='utf-8')
        child = Child([exe, 'acp'], cwd=directory)
        reply = []
        session = None
        def send(ident, method, params):
            child.send({'jsonrpc': '2.0', 'id': ident, 'method': method, 'params': params})
        def start_prompt():
            import base64, mimetypes
            content=[{'type':'text','text':prompt}]
            for path in images:
                raw=Path(path).read_bytes()
                if len(raw)>4_000_000:raise RuntimeError('Reference image exceeds 4 MB')
                content.append({'type':'image','data':base64.b64encode(raw).decode(),'mimeType':mimetypes.guess_type(path)[0] or 'image/jpeg'})
            send(4, 'session/prompt', {'sessionId': session, 'prompt': content})
        try:
            send(0, 'initialize', {'protocolVersion': 1, 'clientCapabilities': {},
                                 'clientInfo': {'name': 'geppetto_sbox', 'version': '1.0'}})
            for event in child.events():
                if event.get('error'):
                    raise RuntimeError('OpenCode: ' + event['error'].get('message', 'request failed'))
                if event.get('id') == 0 and 'result' in event:
                    send(1, 'session/new', {'cwd': directory, 'mcpServers': []})
                elif event.get('id') == 1 and 'result' in event:
                    session = event['result']['sessionId']
                    send(2, 'session/set_mode', {'sessionId': session, 'modeId': 'geppetto-native'})
                elif event.get('id') == 2 and 'result' in event:
                    if '#' in model:
                        send(3, 'session/set_config_option', {'sessionId': session, 'configId': 'effort', 'value': model.split('#', 1)[1]})
                    else:
                        start_prompt()
                elif event.get('id') == 3 and 'result' in event:
                    start_prompt()
                elif event.get('id') == 4 and 'result' in event:
                    reason = event['result'].get('stopReason')
                    if reason != 'end_turn':
                        raise RuntimeError('OpenCode stopped: ' + str(reason))
                    if not ''.join(reply).strip():
                        raise RuntimeError('OpenCode returned no text')
                    return ''.join(reply)
                if event.get('method') == 'session/update':
                    update = event.get('params', {}).get('update', {})
                    kind = update.get('sessionUpdate')
                    if kind == 'agent_message_chunk':
                        content = update.get('content', {})
                        if content.get('type') == 'text':
                            reply.append(content.get('text', ''))
                            text_delta(content.get('text', ''))
                    elif kind == 'agent_thought_chunk':
                        thinking()
                    elif kind in ('tool_call', 'tool_call_update'):
                        raise RuntimeError('OpenCode attempted a tool; native chat only accepts modelling replies')
                    elif update.get('_meta', {}).get('opencode/retry'):
                        raise RuntimeError('OpenCode provider failed; automatic retries stopped')
                elif 'id' in event and event.get('method'):
                    raise RuntimeError('OpenCode requested an unsupported interaction: ' + event['method'])
        finally:
            child.close()
