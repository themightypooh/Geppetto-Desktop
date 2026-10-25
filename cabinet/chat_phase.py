"""Explicit, model-reported workflow phases; never guess from incidental prose."""
import re

PHASE_PROMPT = (
    '\nFor a modeling task, announce your current phase with a standalone line '
    '"Phase: Researching" before gathering references or reading modeling guidance. '
    'When research is complete and you are about to create or edit the model, '
    'announce "Phase: Modeling" and briefly say research is complete. '
    'Report transitions as they happen, not a planned or invented phase. '
    'Do not use phase lines for ordinary conversation.'
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
