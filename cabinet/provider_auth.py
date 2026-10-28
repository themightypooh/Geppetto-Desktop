"""User-initiated account changes in the provider's own terminal UI.

No credentials are read, captured or saved by Geppetto. Commands are fixed argv,
never a shell, and no authentication happens until the user confirms in terminal.
"""
import argparse
import re
import shutil
import subprocess
import sys
from pathlib import Path


COMMANDS = {
    'claude': (('auth', 'logout'), ('auth', 'login')),
    'codex': (('logout',), ('login',)),
    'grok': (('logout',), ('login',)),
    'cursor': (('logout',), ('login',)),
    # V2 login adds an account; do not delete saved accounts to switch providers.
    'opencode': (('auth', 'login'),),
}
PROGRAMS = {'cursor': 'cursor-agent'}


def account_info(model):
    model = model or {}
    kind = model.get('kind', '')
    family = {'claude-cli': 'claude', 'grok-cli': 'grok'}.get(kind, model.get('family', ''))
    if kind not in ('claude-cli', 'grok-cli', 'roundtable-cli'):
        family = ''
    if family in COMMANDS:
        target = model.get('model', '').partition('/')[0] if family == 'opencode' and '/' in model.get('model', '') else ''
        if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]*', target):
            target = ''
        return dict(supported=True, family=family,
                    target=target,
                    message='Uses your shared CLI login, including outside Geppetto. Confirm in the terminal; choose the other account in your browser.')
    if not model:
        message = 'Choose a provider to manage its account.'
    elif kind == 'ollama' or '127.0.0.1' in model.get('endpoint', ''):
        message = 'Local provider — no account sign-in needed.'
    elif model.get('key_env'):
        message = 'API-key provider — change its key in your provider configuration, then Refresh.'
    else:
        message = 'This provider manages sign-in in its own app or bridge. Sign in there, then Refresh.'
    return dict(supported=False, family='', message=message)


def terminal_command(family, target=''):
    if family not in COMMANDS:
        raise RuntimeError('This provider has no supported CLI sign-in flow.')
    if not shutil.which(PROGRAMS.get(family, family)):
        raise RuntimeError('Provider CLI is not installed. Install it, then Refresh providers.')
    runner = [sys.executable, str(Path(__file__).resolve()), '--family', family]
    if target:
        if family != 'opencode' or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]*', target):
            raise RuntimeError('Invalid provider integration for sign-in.')
        runner += ['--target', target]
    # Each terminal waits for the child, so the host can refresh after it closes.
    for name, flags in [('konsole', ['--separate', '--nofork', '-e']),
                        ('gnome-terminal', ['--wait', '--']),
                        ('xterm', ['-e'])]:
        exe = shutil.which(name)
        if exe:
            return [exe] + flags + runner
    raise RuntimeError('No supported terminal found (Konsole, GNOME Terminal or xterm). Run the provider login in your terminal, then Refresh.')


def authenticate(family, read=input, run=subprocess.run, target=''):
    steps = COMMANDS[family]
    if target:
        if family != 'opencode' or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]*', target):
            return 1
        steps = (('auth', 'login', target),)
    exe = shutil.which(PROGRAMS.get(family, family))
    if not exe:
        print('Provider CLI is not installed.')
        return 1
    print('Geppetto — ' + family.title() + ' account sign-in')
    print('This changes the CLI account shared with other apps on this machine.')
    if len(steps) > 1:
        print('Your current account will be signed out first. Cancelling login leaves it signed out.')
    else:
        print('Choose the provider to add/sign in to. Existing saved accounts are kept.')
    print('Use the other account in your browser (switch accounts there if needed).')
    try:
        if read('Continue? [y/N] ').strip().lower() not in ('y', 'yes'):
            return 2
        for args in steps:
            result = run([exe, *args])  # inherit terminal IO; never capture tokens/URLs
            if result.returncode:
                print('Account action did not complete. Check the provider message above.')
                read('Press Enter to close and return to Geppetto. ')
                return 1
        print('Provider sign-in completed. Return to Geppetto to continue your chat.')
        read('Press Enter to close. ')
        return 0
    except (EOFError, KeyboardInterrupt):
        return 2
    except OSError:
        print('Could not start the provider CLI. No further account action was attempted.')
        return 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--family', required=True, choices=tuple(COMMANDS))
    parser.add_argument('--target', default='')
    args = parser.parse_args()
    sys.exit(authenticate(args.family, target=args.target))
