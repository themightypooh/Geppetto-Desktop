"""Explicit, model-reported workflow phases; never guess from incidental prose."""
import re

# Recognize phase reports if a model chooses to send them; don't prescribe a workflow.
PHASE_PROMPT = ''

# Shared by every tool-capable seat (including the Muse file bridge) and the
# in-viewport Claude card. This is preparation before an accept intent, not a
# second user-facing planning turn or permission to skip the live-run gate.
BUILD_PREP_PROMPT = (
    '\nFollow the user\'s request; choose your own modeling approach. '
    'Use the tool documentation when needed. Prepare the '
    'executable script and preflight it BEFORE the toast/card is shown, then '
    'file ONE intent for the exact script/subject/pane. After Yes, execute that '
    'exact approved script promptly in its assigned pane; do not research, rewrite, '
    'or substitute it, and do not spend the approval window preparing another '
    'script. No means no build. If the user stops the work or the chest closes, '
    'stop at once; do not silently retry, restart, or start a second or nested '
    'provider turn \u2014 the user chooses any second attempt. If a required '
    'tool is missing, report it. This applies equally to models working inside the '
    'cabinet and agents connecting from outside it.\n'
)

# Plan/read-only boundary, shared by the card and (via SYSTEM_PLAN) the panel paths.
# This text must never carry build preparation: it is the exclusion side of the gate.
PLAN_BOUNDARY_PROMPT = (
    '\nPlan/read-only turn: discuss and inspect only. Do not file a Geppetto model '
    'intent, do not claim build permission, do not edit files, execute builds, '
    'or claim a model was created.\n'
)
_PHASE = re.compile(r'^Phase: (Researching|Modeling)\s*$', re.MULTILINE | re.IGNORECASE)


def reported_phase(rows):
    phase = ''
    for row in rows:
        if row.get('role') == 'you':
            phase = ''
        elif row.get('role') == 'claude':
            for match in _PHASE.finditer(row.get('text', '')):
                phase = match[1].lower()
    return phase
