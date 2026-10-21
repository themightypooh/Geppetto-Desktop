#!/usr/bin/env node
/**
 * Install a character card into the native LocalChat app's character store.
 *
 *   node tools/cards/install-into-localchat.mjs --card sable-harrow
 *
 * LocalChat keeps its cast in ~/.local/share/local-chat/characters.json as
 * { name, tagline, system, greeting }. A SillyTavern card carries more than
 * that, so the card is flattened here:
 *
 *   system    = description + personality + scenario + world + examples + rules
 *   greeting  = first_mes
 *   tagline   = the card's scenario
 *
 * The world and the example messages are not optional extras. SillyTavern
 * injects the lorebook on keywords and the example messages as a few-shot
 * block, and a 12B leans on the examples harder than it leans on the
 * instructions - losing them is the difference between a card that behaves
 * and a card that does not. There is no keyword engine here, so every lorebook
 * entry goes in.
 *
 * The file is merged, not replaced: the app's built-in cast is kept and only
 * the entry with the same name is replaced.
 */

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const STORE = path.join(os.homedir(), '.local', 'share', 'local-chat', 'characters.json');

const slug = process.argv.includes('--card')
    ? process.argv[process.argv.indexOf('--card') + 1]
    : 'sable-harrow';

const cardPath = path.join(HERE, slug, 'card.json');
if (!fs.existsSync(cardPath)) {
    console.error(`install-into-localchat: no card at ${cardPath}`);
    process.exit(1);
}

const card = JSON.parse(fs.readFileSync(cardPath, 'utf8'));
const data = card.data ?? card;

// The native app has no macro engine, so SillyTavern's {{user}} and {{char}}
// placeholders would reach the model as literal text.
const expand = (text) => String(text ?? '')
    .replaceAll('{{user}}', 'the user')
    .replaceAll('{{char}}', data.name)
    .replaceAll('{{charIfNotGroup}}', data.name);

// The world: the lorebook. SillyTavern injects these on keywords; here they
// are all in, because there is no keyword engine to do it with.
const world = Array.isArray(data.character_book?.entries)
    ? data.character_book.entries
        .filter((entry) => entry.enabled !== false)
        .map((entry) => `[${entry.comment ?? 'World'}]\n${entry.content}`)
        .join('\n\n')
    : '';

const system = [
    data.description,
    data.personality,
    data.scenario,
    world ? `World:\n\n${world}` : '',
    data.mes_example ? `Example messages:\n\n${data.mes_example}` : '',
    data.post_history_instructions,
].filter(Boolean).map(expand).join('\n\n');

const entry = {
    name: data.name,
    tagline: expand(data.scenario ?? '').split('\n')[0].slice(0, 160),
    system,
    greeting: expand(data.first_mes ?? ''),
    // The tracker pass only needs to know who the character is and how they
    // behave, not the example messages and the whole world. Sending the full
    // system prompt costs seconds of prefill for a pass that only reads a scene
    // and counts a few numbers.
    trackerContext: expand([
        data.description,
        data.personality,
        ...(data.character_book?.entries ?? [])
            .filter((e) => e.enabled !== false && e.constant)
            .map((e) => `[${e.comment ?? 'World'}]\n${e.content}`),
    ].filter(Boolean).join('\n\n')),
};

let store = { Characters: [] };
if (fs.existsSync(STORE)) {
    try {
        const parsed = JSON.parse(fs.readFileSync(STORE, 'utf8'));
        if (Array.isArray(parsed.Characters)) store = parsed;
    } catch {
        console.warn('install-into-localchat: the existing store was unreadable, starting a new one');
    }
}

const index = store.Characters.findIndex(
    (c) => String(c.name).toLowerCase() === entry.name.toLowerCase(),
);
if (index >= 0) store.Characters[index] = entry;
else store.Characters.push(entry);

fs.mkdirSync(path.dirname(STORE), { recursive: true });
fs.writeFileSync(STORE, JSON.stringify(store, null, 2) + '\n');

console.log(`store : ${STORE}`);
console.log(`added : ${entry.name} (${store.Characters.length} characters)`);
console.log(`system: ${entry.system.length} chars, greeting: ${entry.greeting.length} chars`);
