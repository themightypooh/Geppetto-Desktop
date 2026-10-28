#!/usr/bin/env python3
"""Bundled Codex/OpenCode adapters. No SDK, Model Hub, account or key handling.

The caller supplies its workspace as cwd and its isolated CLI environment. Prompts
arrive on stdin, never in argv. The final Claude-style result is understood by the
cabinet's existing chat reader. The embedded integration can opt into public text streaming with --stream.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile

# Token guard 2026-10-25: a CLI turn must never run forever. chat.py's guard
# already kills a live turn after 1 hour, but if Geppetto itself dies or is
# closed, that guard dies with it and this shim (started in its own session)
# would otherwise keep a Codex/OpenCode agent running with no one watching.
# This ceiling matches the live guard, so normal turns are unchanged; it only
# stops an orphaned or looping turn from burning usage indefinitely.
PROVIDER_TIMEOUT_SECS = 3600
EFFORTS = ('minimal', 'low', 'medium', 'high', 'xhigh')


def run(provider, model, prompt, act=False, effort=''):
    if provider not in ('codex', 'opencode'):
        raise RuntimeError('Unsupported CLI provider: ' + provider)
    exe = shutil.which(provider)
    if not exe:
        raise RuntimeError(provider + ' CLI is not installed; install it and sign in with your own account.')
    # TemporaryDirectory is private (0700); prompts and responses are not argv.
    # Never change cwd: the parent owns workspace selection and permissions.
    with tempfile.TemporaryDirectory(prefix='geppetto-provider-') as directory:
        if provider == 'codex':
            output = Path(directory) / 'reply.txt'
            args = [exe, 'exec', '--ephemeral', '--sandbox',
                    'workspace-write' if act else 'read-only', '--skip-git-repo-check',
                    '-o', str(output)]
            if model:
                args += ['-m', model]
            # Reasoning effort is a token lever, not a quality default: when
            # the caller passes one (Plan/chat turns pass 'low'), pin it here
            # instead of letting a config or model default run high. Build
            # turns pass nothing and inherit the user's own Codex setting
            # (medium by default), so modelling quality is untouched.
            if effort in EFFORTS:
                args += ['-c', 'model_reasoning_effort="%s"' % effort]
            args += ['-']
            try:
                result = subprocess.run(args, input=prompt, capture_output=True, text=True,
                                        timeout=PROVIDER_TIMEOUT_SECS)
            except subprocess.TimeoutExpired:
                # subprocess.run already killed the CLI process; if this shim
                # is its own session leader (as Geppetto starts it), kill the
                # whole process group too, so a Codex tool child cannot keep
                # running and burning usage after the timeout. A shim run by
                # hand in a terminal is not a session leader, so this step
                # is skipped there and cannot kill the user's shell.
                try:
                    if os.getsid(0) == os.getpid():
                        os.killpg(os.getpid(), signal.SIGKILL)
                except OSError:
                    pass
                raise RuntimeError(provider + ': timed out after %d seconds; no reply, no further usage' % PROVIDER_TIMEOUT_SECS)
        elif provider == 'opencode':
            request = Path(directory) / 'request.txt'
            request.write_text(prompt, encoding='utf-8')
            os.chmod(request, 0o600)
            args = [exe, 'run', '--agent', 'build' if act else 'plan', '--auto',
                    '--format', 'default', '--file', str(request)]
            if model:
                args += ['--model', model]
            args += ['Follow the attached request, including its instructions and conversation history.']
            try:
                result = subprocess.run(args, stdin=subprocess.DEVNULL, capture_output=True, text=True,
                                        timeout=PROVIDER_TIMEOUT_SECS)
            except subprocess.TimeoutExpired:
                try:
                    if os.getsid(0) == os.getpid():
                        os.killpg(os.getpid(), signal.SIGKILL)
                except OSError:
                    pass
                raise RuntimeError(provider + ': timed out after %d seconds; no reply, no further usage' % PROVIDER_TIMEOUT_SECS)
        if result.returncode:
            # Retain the CLI's own authentication/quota diagnostic, not a fake reply.
            raise RuntimeError(provider + ': ' + ((result.stderr or '').strip()[-600:] or
                                               'CLI exited ' + str(result.returncode)))
        reply = ((output.read_text(encoding='utf-8') if output.exists() else '')
                 if provider == 'codex' else result.stdout).strip()
        if not reply:
            raise RuntimeError(provider + ' returned no text')
        return reply


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--provider', choices=('codex', 'opencode'), required=True)
    parser.add_argument('--model', default='')
    parser.add_argument('--act', action='store_true')
    parser.add_argument('--image', action='append', default=[], help='Selected reference image for the embedded transport')
    parser.add_argument('--stream', action='store_true', help='Owned, script-only live transport for embedded chat')
    parser.add_argument('--effort', default='', choices=[''] + list(EFFORTS),
                        help='Codex reasoning effort; empty inherits the user config')
    args = parser.parse_args(argv)
    try:
        prompt = sys.stdin.read()
        if args.stream:
            if args.act:
                raise RuntimeError('--stream does not permit workspace actions')
            from provider_stream import codex, opencode
            exe = shutil.which(args.provider)
            if not exe:
                raise RuntimeError(args.provider + ' CLI is not installed')
            reply = (codex(exe, args.model, prompt, args.effort, args.image) if args.provider == 'codex'
                     else opencode(exe, args.model, prompt, args.image))
        else:
            reply = run(args.provider, args.model, prompt, args.act, args.effort)
    except (OSError, RuntimeError, UnicodeError) as error:
        print(json.dumps({'type': 'result', 'is_error': True, 'result': str(error)}), flush=True)
        return 1
    print(json.dumps({'type': 'result', 'result': reply}), flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())
