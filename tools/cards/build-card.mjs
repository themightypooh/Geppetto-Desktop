#!/usr/bin/env node
/**
 * Build an installable SillyTavern character card from an editable card.json.
 *
 *   node tools/cards/build-card.mjs [--card <slug>] [--image <file>]
 *
 * The card text lives in tools/cards/<slug>/card.json and is the single source
 * of truth. This script embeds it into a PNG using SillyTavern's own
 * character-card writer (src/character-card-parser.js), so the result is
 * byte-for-byte what the app expects: a 'chara' chunk for spec V2 and a 'ccv3'
 * chunk for spec V3.
 *
 * The PNG is written into the local SillyTavern build at
 * SillyTavern/data/default-user/characters/<Name>.png and is verified by
 * reading it back with SillyTavern's own reader.
 */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..', '..');
const ST = path.join(ROOT, 'SillyTavern');
const CHARACTERS_DIR = path.join(ST, 'data', 'default-user', 'characters');

const { write: writeCardPng, read: readCardPng } = await import(
    path.join(ST, 'src', 'character-card-parser.js')
);
const { TavernCardValidator } = await import(
    path.join(ST, 'src', 'validator', 'TavernCardValidator.js')
);

const REQUIRED_DATA_FIELDS = [
    'name', 'description', 'personality', 'scenario', 'first_mes', 'mes_example',
    'creator_notes', 'system_prompt', 'post_history_instructions',
    'alternate_greetings', 'tags', 'creator', 'character_version', 'extensions',
];

function parseArgs(argv) {
    const opts = { slug: 'vesper-quill', image: null };
    for (let i = 0; i < argv.length; i++) {
        if (argv[i] === '--card' && argv[i + 1]) opts.slug = argv[++i];
        else if (argv[i] === '--image' && argv[i + 1]) opts.image = argv[++i];
    }
    return opts;
}

function fail(message) {
    console.error(`build-card: ${message}`);
    process.exit(1);
}

const opts = parseArgs(process.argv.slice(2));
const cardDir = path.join(HERE, opts.slug);
const cardPath = path.join(cardDir, 'card.json');
const imagePath = opts.image
    ? path.resolve(process.cwd(), opts.image)
    : path.join(cardDir, 'portrait.png');

if (!fs.existsSync(cardPath)) fail(`no card.json at ${cardPath}`);
if (!fs.existsSync(imagePath)) fail(`no image at ${imagePath}`);

const card = JSON.parse(fs.readFileSync(cardPath, 'utf8'));
const data = card.data ?? card;

// ---------------------------------------------------------------- validate
const missing = REQUIRED_DATA_FIELDS.filter((field) => !(field in data));
if (missing.length) fail(`card is missing required data fields: ${missing.join(', ')}`);
if (!Array.isArray(data.alternate_greetings)) fail('data.alternate_greetings must be an array');
if (!Array.isArray(data.tags)) fail('data.tags must be an array');
if (typeof data.extensions !== 'object' || data.extensions === null) {
    fail('data.extensions must be an object');
}
const book = data.character_book;
if (book) {
    if (!Array.isArray(book.entries)) fail('data.character_book.entries must be an array');
    if (typeof book.extensions !== 'object' || book.extensions === null) {
        fail('data.character_book.extensions must be an object');
    }
}

const validator = new TavernCardValidator(card);
const spec = validator.validate();
if (!spec) fail(`card failed TavernCardValidator: ${validator.lastValidationError}`);

// ------------------------------------------------------------------ embed
const image = fs.readFileSync(imagePath);
let png;
try {
    png = writeCardPng(image, JSON.stringify(card));
} catch (error) {
    fail(`SillyTavern could not write the card PNG: ${error.message}`);
}

// ----------------------------------------------------------------- install
fs.mkdirSync(CHARACTERS_DIR, { recursive: true });
const outName = `${data.name}.png`;
const outPath = path.join(CHARACTERS_DIR, outName);
fs.writeFileSync(outPath, png);

// ------------------------------------------------------------------ verify
const roundTrip = JSON.parse(readCardPng(fs.readFileSync(outPath)));
const roundData = roundTrip.data ?? roundTrip;
const drift = REQUIRED_DATA_FIELDS.filter(
    (field) => JSON.stringify(roundData[field]) !== JSON.stringify(data[field]),
);
if (drift.length) fail(`round-trip mismatch in: ${drift.join(', ')}`);

const roundSpec = new TavernCardValidator(roundTrip).validate();
if (!roundSpec) fail('the written card no longer validates');

console.log(`card      : ${cardPath}`);
console.log(`image     : ${imagePath}`);
console.log(`installed : ${outPath}`);
console.log(`spec      : v${spec} (round-trip v${roundSpec})`);
console.log(`name      : ${data.name}`);
console.log(`book      : ${book ? `${book.entries.length} entries` : 'none'}`);
console.log(`size      : ${(png.length / 1024).toFixed(1)} KiB`);
