#!/usr/bin/env node
/**
 * Configure the SillyTavern extensions that support the Andrea card.
 *
 *   node tools/chat/configure-andrea-extensions.mjs [--dry-run]
 *
 * Everything is written into SillyTavern/data/default-user/settings.json under
 * `extension_settings`, plus one regex script that keeps the position tracker
 * last in her reply. The file is backed up first, and the script is idempotent:
 * running it twice produces the same result.
 *
 * Why the settings are written here and not clicked in the UI: every key below
 * was read out of the extension's own source in
 * SillyTavern/data/default-user/extensions/<name>/, so the shapes are the
 * ones the extensions actually expect.
 */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..', '..');
const SETTINGS = path.join(ROOT, 'SillyTavern', 'data', 'default-user', 'settings.json');

// Must match data.create_date in tools/cards/andrea/card.json. SillyTavern
// falls back to the PNG's file timestamp when a card has no create_date, and
// that changes on every rebuild - which would orphan the per-character State
// config below. The card pins it instead.
const ANDREA_CREATE_DATE = '2026-10-20T00:00:00.000Z';
const STATE_KEY = `Andrea-${ANDREA_CREATE_DATE}`;

const KOBOLDCPP_BASE = 'http://127.0.0.1:5001';
const KOBOLDCPP_MODEL = 'koboldcpp/MN-12B-Mag-Mell-R1-Uncensored-Scale1.2.i1-IQ4_XS';

// ---------------------------------------------------------------------------
// The position tracker, as a second pass rather than a line the character has
// to remember to write. The State extension sends this after every turn,
// reads the chat, and drops the result into the chat as one small rolling
// system message. It is the authoritative position; the bracket line at the
// end of her reply is the cheap in-context echo of it.
// ---------------------------------------------------------------------------
const STATE_PROMPT = [
    'Read the conversation above and report where Andrea physically is right now, at the end of the last thing she did.',
    'Reply with exactly one line, in this format, and nothing else:',
    '',
    '[ANDREA: <place> | <distance from the user> | <what her hands are doing> | <which way she is facing>]',
    '',
    'Short plain words only: landing, kitchen, doorway, couch, hallway, 2m, 4m, hand on the frame, arms folded, facing you, facing away, turned half away.',
    'If she has not moved since the last state line, repeat those values unchanged.',
    'Never explain. Never add a second line.',
].join('\n');

// ---------------------------------------------------------------------------
// Recast. Only the Character Behavior Validator pass is left on.
//
// That pass is the one that matters for this card: its banned_behaviours
// block explicitly forbids "asking for a compensation" and says characters
// should not "resolve tension the character would hold" - which is exactly
// the "she is not resisting enough" problem, enforced by a second pass
// instead of by hoping the 12B remembers the instruction.
//
// Prose Rhythm and Grounding are off. Prose Rhythm rewrites the narration,
// which fights the card's fixed voice and can drop the tracker line, and it
// is another full generation per message on a 12B.
// ---------------------------------------------------------------------------
const RECAST_VALIDATOR_PROMPT = `You are a character consistency editor. Your only job is to fix dialog and actions that are not in character in <text_to_transform>. Do not improve prose. Do not fix grammar. Do not restructure sentences. Keep in mind you may not have received the whole scene context.
Priority order for character signals: example dialogue > personality traits > general description > scene context.

Fix text if it:
- Uses phrasing that contradicts the example dialogue voice
- Has the character act warmer, cooler, more helpful, or more dramatic than the card defines
- Responds only to the surface of what was said, ignoring what the other character is visibly feeling
- States emotion directly instead of showing it through behavior or word choice
- Resolves tension the character would hold

<banned_behaviors>
Also following are behaviors from characters that should be modified or removed completely:
- Asking for a compensation, any kind of 'Okay but give me this', should be avoided and exchanged to something else. Compliance is not easily bought.
- Stiff unexpected behavior from characters. Characters should not stop and ask things if it doesn't fit them or the context.
- Writing the character as eager, grateful, compliant or melting. Warmth does not open this character, it closes her.
- Making the character explain themselves, ask for anything out loud, or thank the other person with any conviction.
</banned_behaviors>

Return only the corrected text. No explanations, no commentary.`;

const RECAST_PRESET = {
    name: 'Andrea - Character Consistency Only',
    passes: [
        {
            id: 'pass_grounding',
            name: '⛓️ Grounding',
            enabled: false,
            contextLength: 3,
            prompt: '',
            connection: '',
            injectWorldInfo: true,
            includeCharCard: true,
            includeSceneContext: true,
        },
        {
            id: 'pass_validator',
            name: '✅ Character Behavior Validator',
            enabled: true,
            contextLength: 7,
            prompt: RECAST_VALIDATOR_PROMPT,
            connection: '',
            injectWorldInfo: false,
            includeCharCard: true,
            includeSceneContext: true,
        },
        {
            id: 'pass_prose',
            name: '✒️ Prose Rhythm',
            enabled: false,
            contextLength: 13,
            prompt: '',
            connection: '',
            injectWorldInfo: false,
            includeCharCard: false,
            includeSceneContext: true,
        },
        {
            id: 'pass_repetitionhammer',
            name: '🔨 Repetition Hammer',
            enabled: false,
            contextLength: 35,
            prompt: '',
            connection: '',
            injectWorldInfo: false,
            includeCharCard: false,
            includeSceneContext: true,
        },
    ],
};

// ---------------------------------------------------------------------------
// SimTracker. The HUD half of the tracker.
//
// The tracker data is generated by a *secondary LLM* pointed at the same
// KoboldCpp, not by asking Andrea to emit JSON inside her prose. That keeps
// the card's reply clean - one bracket line, nothing else - while still
// giving the model a live read on her state.
//
// The provider has to be "custom". The extension's "openai" provider ignores
// secondaryLLMEndpoint, posts to api.openai.com, and demands an OpenAI key
// out of SillyTavern's secrets. The "custom" branch uses the endpoint
// verbatim, so it has to be the full URL, and it insists on a non-empty key
// even though KoboldCpp ignores the header.
//
// The prompt lives in the template file rather than here, because the
// extension prefers templateData.sysPrompt over the datingSimPrompt setting
// and would silently discard it. It is kept here as the fallback, and the
// two must stay in sync.
// ---------------------------------------------------------------------------
// The prompt and the field list live in one file, shared with
// tools/cards/build-andrea-tracker-template.mjs, so the template on disk and
// the settings can never drift apart.
const {
    sysPrompt: SIMTRACKER_SYSTEM_PROMPT,
    fields: SIMTRACKER_FIELDS,
} = JSON.parse(
    fs.readFileSync(path.join(ROOT, 'tools', 'cards', 'andrea', 'tracker-prompt.json'), 'utf8'),
);

// ---------------------------------------------------------------------------
// SuperObjective. Andrea pursues her own goals instead of only reacting.
//
// The card can tell her to be hard to get; this makes her act on it. The
// task list itself lives in chat_metadata and is created per chat in the UI,
// but the prompt set that generates and judges those tasks is configured here.
// ---------------------------------------------------------------------------
const OBJECTIVE_PROMPTS = {
    createTask: 'Ignore previous instructions. Please generate a numbered list of plain text tasks to complete an objective. The objective that you must make a numbered task list for is: "{{objective}}". The tasks created should take into account the character traits of {{char}}. These tasks may or may not involve {{user}} directly. Include the objective as the final task.\n\nThe list should be formatted using a number followed by a fullstop and the task on each line, e.g. "1. Take over the world". Include only the list in your reply.',
    checkTaskCompleted: 'Ignore previous instructions. Determine if this task is completed: [{{task}}]. To do this, examine the most recent messages. Your response must only contain either true or false, and nothing else. Example output: true',
    currentTask: 'Your current task is [{{task}}]. Balance existing roleplay with completing this task. The task is what {{char}} is privately trying to achieve. She will not announce it, will not explain it, and will not ask for help with it.',
    completedTasks: 'Recently completed tasks: {{completedTasks}}',
    upcomingTasks: 'Upcoming tasks: {{upcomingTasks}}',
    additionalTasks: 'Ignore previous instructions. Please generate additional numbered tasks to complete the objective: "{{objective}}". The tasks created should take into account the character traits of {{char}}. These tasks may or may not involve {{user}} directly.\n\nThe following tasks have already been created:\n{{existingTasks}}\n\nPlease generate additional tasks that complement these existing tasks. Continue the numbering from where the list left off. Do not repeat any existing tasks.\n\nThe list should be formatted using a number followed by a fullstop and the task on each line, e.g. "4. Investigate the mysterious cave". Include only the list in your reply.',
};

// ---------------------------------------------------------------------------
// The tracker-protection regex.
//
// The card requires the tracker line to be the last thing in her reply. If
// the model puts anything after it, or wanders off and writes a second one,
// this trims everything from the first tracker line onwards so the reply
// always ends on the tracker.
// ---------------------------------------------------------------------------
const TRACKER_REGEX_SCRIPT = {
    id: 'andrea-tracker-last',
    scriptName: 'Andrea - tracker ends the reply',
    findRegex: '/(\\[ANDREA:[^\\]]*\\])[\\s\\S]*$/',
    replaceString: '$1',
    trimStrings: [],
    placement: [2], // AI output
    disabled: false,
    markdownOnly: false,
    promptOnly: false,
    runOnEdit: true,
    substituteRegex: 0,
    minDepth: null,
    maxDepth: null,
};

// ---------------------------------------------------------------------------

const dryRun = process.argv.includes('--dry-run');

const ST_URL = process.env.ST_URL || 'http://127.0.0.1:8000';

/**
 * Read and write settings through SillyTavern's own API rather than editing
 * settings.json on disk.
 *
 * The server owns that file. Editing it underneath a running server works
 * right up until the browser posts its own in-memory copy back and silently
 * discards the change. Going through the API means the server is the one
 * doing the write, and the merge below is applied to whatever the server
 * currently holds - including anything the browser saved since we started.
 */
async function stApi(pathname, body) {
    const tokenResponse = await fetch(`${ST_URL}/csrf-token`);
    const { token } = await tokenResponse.json();
    const cookie = tokenResponse.headers.getSetCookie()
        .map((c) => c.split(';')[0])
        .join('; ');

    const response = await fetch(`${ST_URL}${pathname}`, {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json',
            'x-csrf-token': token,
            ...(cookie ? { cookie } : {}),
        },
        ...(body ? { body: JSON.stringify(body) } : {}),
    });

    if (!response.ok) {
        throw new Error(`${pathname} returned ${response.status}: ${await response.text()}`);
    }
    return response.json();
}

let settings;

if (fs.existsSync(SETTINGS) && !process.env.ST_USE_API) {
    settings = JSON.parse(fs.readFileSync(SETTINGS, 'utf8'));
} else {
    const envelope = await stApi('/api/settings/get');
    settings = JSON.parse(envelope.settings);
}

const es = settings.extension_settings ??= {};

// -- State: the position tracker ---------------------------------------------
es.State ??= {};
es.State[STATE_KEY] = {
    enabled: true,
    prompts: [
        {
            prompt: STATE_PROMPT,
            template: '{{state}}',
            isSmall: true,
            isDelete: true,
            isCollapsed: true,
        },
    ],
};

// -- Recast: character consistency only ---------------------------------------
es.Recast = {
    ...(es.Recast ?? {}),
    enabled: true,
    autorun: true,
    inject: true,
    replace_inline: false,
    hide_until_last: true,
    dynamic_substitution: false,
    // false = the pass runs once on the finished message, not on streaming
    // chunks. Keeps it off the same GPU as the main generation and stops it
    // rewriting text that is still arriving.
    stream_pipeline: false,
    debug_mode: false,
    disable_editable_diff: true,
    legacy_api: false,
    compatibility_mode: false,
    scene_context_as_roles: false,
    apply_regexes: true,
    apply_regex_context: true,
    min_chars: 60,
    presets: [RECAST_PRESET],
    active_preset: RECAST_PRESET.name,
};

// -- MessageSummarize: keep a 12k context from running out -------------------
es.qvink_memory = {
    ...(es.qvink_memory ?? {}),
    connection_profile: '', // use whatever connection is selected (KoboldCpp)
    auto_summarize: true,
    auto_summarize_delay: 0,
    auto_summarize_batch_size: 4,
    auto_summarize_message_limit: 20,
    auto_summarize_on_swipe: true,
    auto_summarize_on_edit: false,
    auto_summarize_block_generation: true,
    block_chat: true,
    message_length_threshold: 10,
    include_user_messages: false,
    include_system_messages: false,
    summary_injection_threshold: 10,
    summary_injection_threshold_type: 'messages',
    exclude_messages_after_threshold: true,
    keep_last_user_message: true,
    separate_long_term: false,
};

// -- SimTracker: the HUD ----------------------------------------------------
es['silly-sim-tracker'] = {
    ...(es['silly-sim-tracker'] ?? {}),
    isEnabled: true,
    codeBlockIdentifier: 'sim',
    defaultBgColor: '#6a5acd',
    showThoughtBubble: true,
    templateFile: 'andrea-tracker.json',
    templatePosition: 'BOTTOM',
    datingSimPrompt: SIMTRACKER_SYSTEM_PROMPT,
    displayInstructionsPrompt: '',
    customFields: SIMTRACKER_FIELDS,
    hideSimBlocks: true,
    trackerFormat: 'json',
    useSecondaryLLM: true,
    secondaryLLMMessageCount: 6,
    // "custom", not "openai": the openai provider ignores this endpoint,
    // posts to api.openai.com and wants an OpenAI key from ST secrets.
    secondaryLLMAPI: 'custom',
    secondaryLLMModel: KOBOLDCPP_MODEL,
    // the custom branch uses the endpoint verbatim - full URL, no /v1 added
    secondaryLLMEndpoint: `${KOBOLDCPP_BASE}/v1/chat/completions`,
    // required non-empty by the custom branch; KoboldCpp ignores the header
    secondaryLLMAPIKey: 'local',
    secondaryLLMTemperature: 0.7,
    secondaryLLMTopP: 1,
    secondaryLLMStreaming: false,
    secondaryLLMUseReverseProxy: false,
    secondaryLLMStripHTML: true,
    maxSimBlocksInContext: 3,
    retainTrackerCount: 3,
    enableInlineTemplates: false,
};

// -- SuperObjective: she pursues her own goals -------------------------------
es.objective = {
    ...(es.objective ?? {}),
    customPrompts: {
        ...(es.objective?.customPrompts ?? {}),
        Andrea: { ...OBJECTIVE_PROMPTS },
    },
};

// -- the tracker-protection regex --------------------------------------------
es.regex = Array.isArray(es.regex) ? es.regex : [];
es.regex = es.regex.filter((s) => s?.scriptName !== TRACKER_REGEX_SCRIPT.scriptName);
es.regex.push(TRACKER_REGEX_SCRIPT);

// -- write -------------------------------------------------------------------
if (dryRun) {
    console.log('dry run - nothing written');
} else {
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    const backup = `${SETTINGS}.bak-${stamp}`;
    if (fs.existsSync(SETTINGS)) fs.copyFileSync(SETTINGS, backup);

    if (process.env.ST_USE_API || !fs.existsSync(SETTINGS)) {
        await stApi('/api/settings/save', settings);
        console.log(`saved via : ${ST_URL}/api/settings/save`);
    } else {
        fs.writeFileSync(SETTINGS, JSON.stringify(settings, null, 4) + '\n');
        console.log(`saved to  : ${SETTINGS}`);
    }
    console.log(`backup    : ${backup}`);
}

console.log('');
console.log('configured:');
console.log(`  State["${STATE_KEY}"]      position tracker (second pass, rolling message)`);
console.log('  Recast                   character consistency validator only');
console.log('  qvink_memory             auto-summarise on the current connection');
console.log('  silly-sim-tracker        HUD via secondary LLM -> ' + KOBOLDCPP_BASE);
console.log('  objective                Andrea prompt set for SuperObjective');
console.log('  regex                    tracker-ends-the-reply');
console.log('');
console.log('reload the SillyTavern page to pick all of this up.');
