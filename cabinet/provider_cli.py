#!/usr/bin/env python3
"""Bundled Codex/OpenCode adapters. No SDK, Model Hub, account or key handling.

The caller supplies its workspace as cwd and its isolated CLI environment. Prompts
arrive on stdin, never in argv. The final Claude-style result is understood by the
cabinet's existing chat reader. Native output is buffered, not live tool telemetry.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile


def run(provider, model, prompt, act=False):
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
            args += ['-']
            result = subprocess.run(args, input=prompt, capture_output=True, text=True)
        elif provider == 'opencode':
            request = Path(directory) / 'request.txt'
            request.write_text(prompt, encoding='utf-8')
            os.chmod(request, 0o600)
            args = [exe, 'run', '--agent', 'build' if act else 'plan', '--auto',
                    '--format', 'default', '--file', str(request)]
            if model:
                args += ['--model', model]
            args += ['Follow the attached request, including its instructions and conversation history.']
            result = subprocess.run(args, stdin=subprocess.DEVNULL, capture_output=True, text=True)
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
    args = parser.parse_args(argv)
    try:
        reply = run(args.provider, args.model, sys.stdin.read(), args.act)
    except (OSError, RuntimeError, UnicodeError) as error:
        print(json.dumps({'type': 'result', 'is_error': True, 'result': str(error)}), flush=True)
        return 1
    print(json.dumps({'type': 'result', 'result': reply}), flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())
