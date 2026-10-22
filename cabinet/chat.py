#!/usr/bin/env python3
"""Shared chat/research catalogue. Secrets stay in environment variables or existing key files.

Optional ~/.config/geppetto/chat-models.json is a list of provider records. It augments
live Ollama/KoboldCpp discovery. Records use id, label, kind, endpoint, model and
optionally key_env, env_file, auth_header, extra_body and research (tool support).
"""
import json
import re
import os
import shutil
import socket
import subprocess
import threading
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

CONFIG = os.path.expanduser('~/.config/geppetto/chat-models.json')
SYSTEM = "You are Geppetto's helpful chat assistant. Answer clearly and concretely. Do not claim you changed files or the scene: this chat has no editing tools."
OLLAMA = os.environ.get('OLLAMA_HOST', 'http://127.0.0.1:11434').rstrip('/')
if not OLLAMA.startswith(('http://', 'https://')):
    OLLAMA = 'http://' + OLLAMA

DEFAULT_MODELS = [
    dict(id='reflection/Beam-1.0-501B-A23B', label='Beam 1.0 · Reflection', kind='openai',
         endpoint='https://api.reflection.ai/openai/v1/', model='Beam-1.0-501B-A23B', key_env='REFLECTION_API_KEY'),
    dict(id='deepseek/deepseek-v4-pro', label='DeepSeek V4 Pro · cloud', kind='openai',
         endpoint='https://api.deepseek.com/v1/', model='deepseek-v4-pro', key_env='TEACHER_API_KEY',
         env_file='~/local-llm/.env', extra_body={'thinking': {'type': 'disabled'}}),
    dict(id='mimo/mimo-v2.6-pro', label='MiMo V2.6 Pro · cloud', kind='openai',
         endpoint='https://token-plan-sgp.xiaomimimo.com/v1/', model='mimo-v2.6-pro', key_env='MIMO_API_KEY',
         env_file='~/local-llm/.mimo.env', auth_header='api-key', extra_body={'thinking': {'type': 'disabled'}}),
    # listed even before its key is set, marked (key missing): put MINIMAX_API_KEY=... in ~/local-llm/.minimax.env
    dict(id='minimax/MiniMax-M3', label='MiniMax M3 · cloud', kind='openai', always=True,
         endpoint='https://api.minimax.io/v1/', model='MiniMax-M3', key_env='MINIMAX_API_KEY',
         env_file='~/local-llm/.minimax.env', extra_body={'reasoning_split': True}),
]

# Claude through the claude CLI, on its own login (the Max plan), never an API key
CLAUDE = shutil.which('claude') or os.path.expanduser('~/.local/bin/claude')
CLAUDE_MODELS = [
    dict(id='claude/opus', label='Claude Opus · Max plan', kind='claude-cli', model='opus', endpoint='cli', research=False),
    dict(id='claude/sonnet', label='Claude Sonnet · Max plan', kind='claude-cli', model='sonnet', endpoint='cli', research=False),
    dict(id='claude/haiku', label='Claude Haiku · Max plan', kind='claude-cli', model='haiku', endpoint='cli', research=False),
]


def key(model):
    name = model.get('key_env', '')
    if not name:
        return ''
    value = os.environ.get(name, '')
    if value:
        return value
    if model.get('env_file'):
        try:
            for line in Path(model['env_file']).expanduser().read_text().splitlines():
                k, sep, v = line.removeprefix('export ').partition('=')
                if sep and k.strip() == name:
                    return v.strip().strip('\"').strip("'")
        except OSError:
            pass
    return ''


def _get(url):
    with urllib.request.urlopen(url, timeout=1.5) as response:
        return json.load(response)


def models(discover=True):
    """Installed local models plus configured external models; no network on the UI thread."""
    custom = []
    try:
        data = json.loads(Path(CONFIG).read_text())
        if isinstance(data, list):
            custom = [m for m in data if isinstance(m, dict) and
                      all(isinstance(m.get(k), str) and m[k] for k in ('id', 'endpoint', 'model'))]
    except (OSError, ValueError):
        pass
    out = []
    if discover:
        try:
            for m in _get(OLLAMA + '/api/tags').get('models', []):
                name = m['name']
                size = m.get('details', {}).get('parameter_size', '')
                out.append(dict(id='ollama/' + name, label=('Qwen3.5 4.5B · local chat' if name == 'geppetto-chat:latest' else name.removesuffix(':latest') + ' · ' + size + ' local'),
                                kind='ollama', endpoint=OLLAMA + '/', model=name,
                                research='tools' in m.get('capabilities', []),
                                vision='vision' in m.get('capabilities', []),
                                extra_body={'reasoning_effort': 'none'} if m.get('details', {}).get('family') == 'qwen35' else {}))
        except (OSError, ValueError, KeyError):
            pass
        try:
            for m in _get('http://127.0.0.1:5001/v1/models').get('data', []):
                out.append(dict(id=m['id'], label='RP · ' + m['id'].split('/')[-1], kind='openai',
                                endpoint='http://127.0.0.1:5001/v1/', model=m['id'], research=False))
        except (OSError, ValueError, KeyError):
            pass
    if os.access(CLAUDE, os.X_OK):
        out += [dict(m) for m in CLAUDE_MODELS]
    for m in DEFAULT_MODELS:
        if key(m) or m.get('always'):
            out.append(dict(m))
    # Custom records override discovery/presets, without hiding the user's installed models.
    by_id = {m['id']: m for m in out}
    for m in custom:
        by_id[m['id']] = dict(m)
    out = list(by_id.values())
    for m in out:
        m.setdefault('label', m['model'])
        m.setdefault('kind', 'openai')
        m.setdefault('research', True)
        m['available'] = not m.get('key_env') or bool(key(m))
        if not m['available']:
            m['label'] += ' (key missing)'
    preferred = ['ollama/geppetto-chat:latest', 'ollama/prompt-pocket:latest']
    return sorted(out, key=lambda m: (preferred.index(m['id']) if m['id'] in preferred else 2,
                                     0 if m['kind'] == 'ollama' else 1, m['label'].lower()))


def find(model_id, catalogue=None):
    return next((m for m in (models() if catalogue is None else catalogue) if m['id'] == model_id), None)


def research_arguments(model):
    if not model.get('research', True):
        raise RuntimeError('This model is for chat; research needs tool calling.')
    if model.get('key_env') and not key(model):
        raise RuntimeError('Missing ' + model['key_env'])
    endpoint = model['endpoint'].rstrip('/') + ('/v1/' if model['kind'] == 'ollama' else '/')
    args = ['--provider', 'openai' if model['kind'] == 'ollama' else model['kind'],
            '--endpoint', endpoint, '--model', model['model']]
    if model.get('key_env'):
        args += ['--key-env', model['key_env']]
    if model.get('auth_header'):
        args += ['--auth-header', model['auth_header']]
    if model.get('extra_body'):
        args += ['--request-options', json.dumps(model['extra_body'])]
    if model.get('vision'):
        args.append('--vision')
    return args


def _url(model):
    base = model['endpoint'].rstrip('/') + '/'
    return base + {'ollama': 'api/chat', 'anthropic': 'messages',
                   'google': 'models/' + urllib.parse.quote(model['model'], safe='') + ':generateContent'}.get(model['kind'], 'chat/completions')


NOTES_LIMIT = 6000
API_LIMIT = 4000
EFFIGY_API = os.path.expanduser(os.environ.get('GEPPETTO_EFFIGY_API', '~/games/projects/geppetto/EFFIGY-API.md'))
_WORD = re.compile(r'[a-z][a-z0-9]{2,}')
_STOP = set('the and for with that this what how can you are was not but have from your into does its any all use'.split())


def research_notes(run_dir):
    """The newest design brief a research run saved, as chat background."""
    try:
        briefs = sorted(f for f in os.listdir(run_dir) if f.startswith('task-') and f.endswith('-brief.txt'))
        if not briefs:
            return ''
        with open(os.path.join(run_dir, briefs[-1]), encoding='utf-8', errors='replace') as f:
            text = f.read(NOTES_LIMIT + 1).strip()
    except OSError:
        return ''
    if len(text) > NOTES_LIMIT:
        text = text[:NOTES_LIMIT].rsplit('\n', 1)[0] + '\n[notes truncated]'
    return text


def effigy_reference(question, path=None, limit=API_LIMIT):
    """EFFIGY-API.md sections that share the most words with the question, within the limit."""
    words = set(_WORD.findall(question.lower())) - _STOP
    if not words:
        return ''
    try:
        with open(path or EFFIGY_API, encoding='utf-8', errors='replace') as f:
            text = f.read()
    except OSError:
        return ''
    sections = [p.strip() for p in re.split(r'(?m)^(?=#{2,3} )', text) if p.strip()]
    scored = []
    for i, part in enumerate(sections):
        lines = part.split('\n', 1)
        heading = set(_WORD.findall(lines[0].lower()))
        body = _WORD.findall(part.lower())
        score = sum(3 if w in heading else 0 for w in words) + sum(1 for w in body if w in words) / (1 + len(body) / 400)
        if score > 0:
            scored.append((score, i, part))
    picked, used = [], 0
    for score, i, part in sorted(scored, reverse=True):
        if used + len(part) > limit:
            continue
        picked.append((i, part)); used += len(part)
    return '\n\n'.join(part for i, part in sorted(picked))


def _system(notes, api=''):
    system = SYSTEM
    if notes:
        system += (" Research notes for the player's current project follow. They came from web research:"
                   " use them as reference, cite them when relevant, and say so when they do not cover a question."
                   "\n\n<research_notes>\n" + notes + "\n</research_notes>")
    if api:
        system += ("\n\nExcerpts from Effigy's API reference (Geppetto's modeller) that match the question follow."
                   " Use only names that appear here; if what you need is not in them, say so instead of inventing API."
                   "\n\n<effigy_api>\n" + api + "\n</effigy_api>")
    return system


def _body(model, history, streaming=False, notes='', api=''):
    history = [h for h in history if h['role'] in ('you', 'model')][-16:]
    # Keep whole recent turns, leaving headroom in the local 8k context for notes and the reply.
    while len(history) > 1 and sum(len(h['text']) for h in history) > 16000 - len(notes) - len(api):
        history = history[1:]
    while history and history[0]['role'] != 'you':
        history = history[1:]
    messages = [{'role': 'user' if h['role'] == 'you' else 'assistant', 'content': h['text']} for h in history]
    kind = model['kind']
    system = _system(notes, api)
    if kind == 'anthropic':
        return dict(model=model['model'], max_tokens=2048, system=system, messages=messages)
    if kind == 'google':
        return {'systemInstruction': {'parts': [{'text': system}]},
                'contents': [{'role': 'user' if h['role'] == 'you' else 'model', 'parts': [{'text': h['text']}]} for h in history]}
    body = dict(model=model['model'], messages=[{'role': 'system', 'content': system}] + messages, stream=streaming)
    if kind == 'ollama':
        body.update(think=False, keep_alive='2m', options={'num_ctx': 8192, 'num_predict': 2048})
    else:
        body.update(model.get('extra_body', {}))
    return body


def _reply(kind, data):
    if kind == 'ollama':
        return data.get('message', {}).get('content') or ''
    if kind == 'anthropic':
        return ''.join(b.get('text', '') for b in data.get('content', []) if b.get('type') == 'text')
    if kind == 'google':
        return ''.join(p.get('text', '') for p in (data.get('candidates') or [{}])[0].get('content', {}).get('parts', []) if not p.get('thought'))
    return ((data.get('choices') or [{}])[0].get('message') or {}).get('content') or ''


def _request(model, history, streaming, notes='', api=''):
    secret = key(model)
    if model.get('key_env') and not secret:
        raise RuntimeError('Missing ' + model['key_env'] + '; configure this provider before sending.')
    headers = {'Content-Type': 'application/json'}
    kind = model['kind']
    if kind == 'anthropic':
        headers.update({'x-api-key': secret, 'anthropic-version': '2023-06-01'})
    elif kind == 'google':
        headers['x-goog-api-key'] = secret
    elif secret:
        header = model.get('auth_header', 'Authorization')
        headers[header] = 'Bearer ' + secret if header.lower() == 'authorization' else secret
    return urllib.request.Request(_url(model), data=json.dumps(_body(model, history, streaming, notes, api)).encode(), headers=headers, method='POST')


CLAUDE_HOME = os.path.expanduser('~/.local/share/geppetto/chat')
CLAUDE_TOOLS = ['Read', 'Write', 'Edit', 'Glob', 'Grep', 'Skill', 'WebSearch', 'WebFetch',
                'Bash(geppetto-live *)', 'Bash(python3 */cabinet/host.py *)', 'Bash(ls *)', 'Bash(mkdir *)', 'Bash(cat *)']
CLAUDE_CHAT = ("You are the chat in Geppetto's cabinet (the paper ball), talking to po. You can model:"
               " use the geppetto skill, write Effigy build scripts in this folder and run them with"
               " `geppetto-live build.txt - outdir` so po watches each step in a viewport pane; the run stays open"
               " with the model loaded: change it with `geppetto-live more RUN edit.txt` (a few lines), never a"
               " new run or a rebuild; a bad line holds the run, send the fix with `more`; `geppetto-live close RUN`"
               " when po is happy. po flags steps with notes in the chest; they reach you at your next tool call. Never restart, quit or"
               " take over the Geppetto chest. The chat window is small: keep replies short.")
_claude_sessions = set()      # chat sessions the CLI has already started; later turns --resume them


def _claude_cli(model, history, cancel, timeout, session=''):
    """Claude via `claude -p` on its own login (the Max plan), with modelling tools, one CLI session per chat."""
    history = [h for h in history if h['role'] in ('you', 'model')][-16:]
    while history and history[0]['role'] != 'you':
        history = history[1:]
    resume = bool(session) and session in _claude_sessions
    if resume or len(history) == 1:
        prompt = history[-1]['text']
    else:     # the chat began with another model: hand Claude what was said so far
        prompt = ('The conversation so far:\n\n' +
                  '\n\n'.join(('User: ' if h['role'] == 'you' else 'Assistant: ') + h['text'] for h in history[:-1]) +
                  '\n\nReply to the user\'s latest message:\n\n' + history[-1]['text'])
    env = {k: v for k, v in os.environ.items() if k not in ('ANTHROPIC_API_KEY', 'ANTHROPIC_AUTH_TOKEN')}
    args = [CLAUDE, '-p', '--model', model['model'], '--output-format', 'stream-json', '--include-partial-messages',
            '--verbose', '--setting-sources', 'user', '--append-system-prompt', CLAUDE_CHAT,
            '--allowedTools', *CLAUDE_TOOLS]
    if session:
        args += ['--resume', session] if resume else ['--session-id', session]
    else:
        args.append('--no-session-persistence')
    os.makedirs(CLAUDE_HOME, exist_ok=True)
    p = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env,
                         cwd=CLAUDE_HOME, text=True)
    timer = threading.Timer(max(timeout, 3600), p.kill)     # a build can take a while
    timer.start()
    said = False
    try:
        p.stdin.write(prompt)
        p.stdin.close()
        for line in p.stdout:
            if cancel is not None and cancel.is_set():
                return
            try:
                data = json.loads(line)
            except ValueError:
                continue
            if session and data.get('type') == 'system':
                _claude_sessions.add(session)
            if data.get('type') == 'stream_event':
                event = data['event']
                delta = event.get('delta', {})
                if delta.get('type') == 'text_delta':
                    said = True
                    yield delta.get('text', '')
                elif event.get('type') == 'content_block_start' and event.get('content_block', {}).get('type') == 'tool_use':
                    yield ('\n\n' if said else '') + '[' + event['content_block'].get('name', 'tool') + ']\n\n'
                    said = False
            elif data.get('type') == 'result' and data.get('is_error'):
                raise RuntimeError('Claude: ' + str(data.get('result') or 'the request failed'))
        if p.wait() != 0:
            raise RuntimeError('claude CLI failed: ' + (p.stderr.read().strip()[-300:] or 'exit %d' % p.returncode))
    finally:
        timer.cancel()
        if p.poll() is None:
            p.kill()


def stream(model, history, cancel=None, timeout=120, notes='', api='', session=''):
    """Yield answer text; native Ollama NDJSON and OpenAI SSE stream immediately."""
    if cancel is not None and cancel.is_set():
        return
    kind = model['kind']
    if kind == 'claude-cli':
        yield from _claude_cli(model, history, cancel, timeout, session)
        return
    streaming = kind in ('ollama', 'openai')
    try:
        with urllib.request.urlopen(_request(model, history, streaming, notes, api), timeout=timeout) as response:
            ctype = response.headers.get('Content-Type', '')
            if not streaming or ('event-stream' not in ctype and kind != 'ollama'):
                data = json.load(response)
                if cancel is None or not cancel.is_set():
                    yield _reply(kind, data)
                return
            for line in response:
                if cancel is not None and cancel.is_set():
                    return
                line = line.decode().strip()
                if not line or line.startswith(('event:', ':')):
                    continue
                if line.startswith('data:'):
                    line = line[5:].strip()
                if line == '[DONE]':
                    break
                data = json.loads(line)
                if data.get('error'):
                    raise RuntimeError('Model server rejected the request. Check model compatibility and available memory.')
                if kind == 'ollama':
                    yield _reply(kind, data)
                else:
                    yield ((data.get('choices') or [{}])[0].get('delta') or {}).get('content') or ''
    except urllib.error.HTTPError as e:
        e.close()
        raise RuntimeError('%s returned HTTP %d. Check model, credentials and quota.' % (model['label'], e.code)) from None
    except (urllib.error.URLError, socket.timeout, TimeoutError):
        raise RuntimeError('Cannot reach %s or request timed out. Check its model server.' % model['label']) from None
    except (ValueError, KeyError, TypeError):
        raise RuntimeError('%s returned an invalid response.' % model['label']) from None


def complete(model, history, timeout=120):
    text = ''.join(stream(model, history, timeout=timeout)).strip()
    if not text:
        raise RuntimeError(model['label'] + ' sent back no text')
    return text


if __name__ == '__main__':
    for m in models():
        print(m['id'], m['label'], 'research' if m['research'] else 'chat only')
