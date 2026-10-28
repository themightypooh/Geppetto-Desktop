"""Observed agent and build activity, without reading private reasoning into the UI.

Incremental, bounded JSONL readers keep control events from hiding an in-flight build.
Only event types and tool names are retained; prompts, arguments and reasoning text are not.
"""
import glob
import json
import os
import re
import time
from datetime import datetime

import live_runs
import pane_owner

CODEX = os.path.expanduser('~/.codex/sessions')
READ_LIMIT = 262144
STALE_AFTER = 120
_files = {}
_sessions = {}
DURABLE = {'starting', 'researching', 'waiting', 'building', 'revision', 'judging',
           'open', 'held', 'checkpoint', 'completed', 'below-target', 'cancelled',
           'failed', 'cancelling', 'operation-failed'}


def timestamp(value, fallback=0):
    try:
        return datetime.fromisoformat(str(value).replace('Z', '+00:00')).timestamp()
    except (ValueError, TypeError):
        return fallback


def events(path):
    """New complete events only; handle partial writes, rotation and truncation."""
    try:
        stat = os.stat(path)
        cached = _files.get(path)
        if cached is None or cached['inode'] != stat.st_ino or stat.st_size < cached['offset']:
            cached = {'inode': stat.st_ino, 'offset': 0, 'agent': {}, 'pending': {}, 'build': {}}
            _files[path] = cached
        start = cached['offset']
        clipped = stat.st_size - start > READ_LIMIT
        if clipped:
            start = stat.st_size - READ_LIMIT
            cached.update(agent={}, pending={}, build={})
        with open(path, 'rb') as stream:
            stream.seek(start)
            if clipped:
                stream.readline()  # do not parse a record cut in half by the bounded tail
            data_start = stream.tell()
            data = stream.read(READ_LIMIT)
        complete = data.rfind(b'\n') + 1
        cached['offset'] = data_start + complete
        out = []
        for line in data[:complete].splitlines():
            try:
                event = json.loads(line)
                if isinstance(event, dict):
                    out.append(event)
            except (ValueError, UnicodeDecodeError):
                continue
        return cached, out, stat.st_mtime
    except OSError:
        return {}, [], 0


def _tool_label(name, source=''):
    # The exec wrapper carries named nested calls. Inspect names, never display arguments.
    nested = re.findall(r'\btools\.([A-Za-z0-9_]+)\s*\(', source) if name in ('exec', 'functions.exec') else []
    names = nested or [name]
    labels = set()
    for tool in names:
        tool = tool.lower()
        if any(x in tool for x in ('request_user_input', 'askuserquestion')):
            labels.add('Waiting for input')
        elif any(x in tool for x in ('apply_patch', 'write_file', 'edit_file')) or tool in ('edit', 'write'):
            labels.add('Editing files')
        elif any(x in tool for x in ('view_image', 'screenshot')):
            labels.add('Inspecting images')
        elif any(x in tool for x in ('web', 'search', 'browser', 'navigate')):
            labels.add('Researching')
        elif any(x in tool for x in ('exec_command', 'write_stdin', 'bash', 'terminal')):
            labels.add('Running a command')
        elif any(x in tool for x in ('read_file', 'read', 'glob', 'grep')):
            labels.add('Reading files')
        elif any(x in tool for x in ('wait', 'sleep')):
            labels.add('Waiting for a tool')
        else:
            labels.add('Using tools')
    return next(iter(labels)) if len(labels) == 1 else 'Using tools'


def _agent_event(cache, event, mtime):
    def mark(label, known=True):
        cache['agent'] = {'label': label, 'seen': timestamp(event.get('timestamp'), mtime), 'known': known}
    payload = event.get('payload') or {}
    if not isinstance(payload, dict):
        return
    kind = payload.get('type')
    if event.get('type') == 'event_msg':
        if kind == 'task_started':
            cache['pending'].clear()
            mark('Processing')
        elif kind in ('task_complete', 'turn_aborted'):
            cache['pending'].clear()
            mark('Waiting for input' if kind == 'task_complete' else 'Stopped')
        elif kind in ('agent_reasoning', 'agent_reasoning_raw_content'):
            mark('Thinking')
    elif event.get('type') == 'response_item':
        if kind in ('function_call', 'custom_tool_call'):
            label = _tool_label(str(payload.get('name') or ''), str(payload.get('input') or ''))
            cache['pending'][payload.get('call_id') or payload.get('id')] = label
            mark(label)
        elif kind in ('function_call_output', 'custom_tool_call_output'):
            cache['pending'].pop(payload.get('call_id'), None)
            mark(next(iter(cache['pending'].values()), 'Processing'))
        elif kind == 'reasoning' and not cache['pending']:
            mark('Thinking')
        elif kind == 'message' and payload.get('role') == 'assistant' and not cache['pending']:
            mark('Responding')
    # Claude Code's transcript carries explicit thinking and tool blocks.
    if event.get('type') == 'assistant':
        message = event.get('message') or {}
        if not isinstance(message, dict):
            return
        for block in message.get('content') or []:
            if not isinstance(block, dict):
                continue
            if block.get('type') == 'tool_use':
                label = _tool_label(str(block.get('name') or ''))
                cache['pending'][block.get('id')] = label
                mark(label)
            elif block.get('type') == 'thinking' and not cache['pending']:
                mark('Thinking')
            elif block.get('type') == 'text' and not cache['pending']:
                mark('Responding')
        if message.get('stop_reason') == 'end_turn':
            cache['pending'].clear()
            mark('Waiting for input')
    elif event.get('type') == 'user':
        message = event.get('message') or {}
        if not isinstance(message, dict):
            return
        blocks = message.get('content') or []
        results = [b for b in blocks if isinstance(b, dict) and b.get('type') == 'tool_result']
        for block in results:
            cache['pending'].pop(block.get('tool_use_id'), None)
        mark(next(iter(cache['pending'].values()), 'Processing'))


def session_path(session, now):
    if not re.fullmatch(r'[a-zA-Z0-9_-]+', session or ''):
        return ''
    hit = _sessions.get(session)
    if hit and ((hit[1] and os.path.exists(hit[1])) or now - hit[0] < 5):
        return hit[1]
    matches = glob.glob(os.path.join(CODEX, '*', '*', '*', '*-' + session + '.jsonl'))
    if not matches:
        matches = glob.glob(os.path.join(pane_owner.PROJECTS, '*', session + '.jsonl'))
    path = max(matches, key=os.path.getmtime) if matches else ''
    _sessions[session] = (now, path)
    return path


def agent(root, now):
    meta = live_runs.run_meta(root)
    session = str(meta.get('session') or '')
    if not session:
        # Older runs did not persist the session, but their process may still name it.
        try:
            pid = int(meta.get('pid') or 0)
            if pid > 0:
                with open('/proc/%d/environ' % pid, 'rb') as stream:
                    env = dict(x.split(b'=', 1) for x in stream.read().split(b'\0') if b'=' in x)
                session = (env.get(b'CODEX_THREAD_ID') or env.get(b'CODEX_SESSION_ID')
                           or env.get(b'CLAUDE_CODE_SESSION_ID') or b'').decode(errors='replace')
        except (OSError, ValueError):
            pass
    if not session:
        session = pane_owner.run_session(root)
    path = session_path(session, now)
    if not path:
        return {'label': 'No agent signal', 'known': False, 'seen': 0, 'source': ''}
    cache, added, mtime = events(path)
    for event in added:
        _agent_event(cache, event, mtime)
    result = dict(cache.get('agent') or {'label': 'No agent signal', 'known': False, 'seen': 0})
    result['source'] = 'session events'
    if result.get('seen') and now - result['seen'] > STALE_AFTER and result['label'] not in ('Stopped', 'Waiting for input'):
        result.update(label='No recent agent signal', known=False)
    return result


def telemetry(root, state, record, now=None):
    now = time.time() if now is None else now
    cache, added, _ = events(os.path.join(root, '.live', 'events.jsonl'))
    for event in added:
        if event.get('state') in DURABLE:
            cache['build'] = event
    effective = cache.get('build') or state
    phase = effective.get('state') or ''
    operation = str(effective.get('operation') or '')
    code = re.sub(r'^line \d+:\s*', '', operation).strip().lower()
    if record.get('status') != 'running':
        label = 'Ended'
    elif record.get('paused'):
        label = 'Paused'
    elif phase == 'held':
        label = 'Needs a fix'
    elif phase == 'checkpoint':
        label = 'Approval needed'
    elif phase == 'open':
        label = 'Waiting for steps'
    elif phase == 'building':
        label = ('Rendering' if code.startswith('render ') else 'Saving' if code.startswith('save ')
                 else 'Exporting' if code.startswith('export ') else 'Building')
    else:
        label = {'starting': 'Starting', 'researching': 'Researching', 'waiting': 'Waiting for model',
                 'revision': 'Between steps', 'judging': 'Judging', 'failed': 'Failed',
                 'completed': 'Completed', 'cancelled': 'Stopped', 'below-target': 'Stopped',
                 'cancelling': 'Stopping', 'operation-failed': 'Needs a fix'}.get(phase, 'Activity unknown')
    observed = agent(root, now)
    # Agent and engine can act concurrently. Preserve both; never infer thinking from a quiet build.
    primary = label
    if label in ('Waiting for steps', 'Between steps', 'Ended', 'Activity unknown') and observed['known']:
        if observed['label'] not in ('Stopped', 'Waiting for input'):
            primary = observed['label']
    return {'label': primary, 'build': label, 'buildPhase': phase, 'operation': operation,
            'buildSeen': timestamp(effective.get('timestamp')), 'outcome': str(effective.get('outcome') or ''), 'agent': observed,
            'detail': 'Agent: %s   ·   Build: %s' % (observed['label'], label)}
