#!/usr/bin/env node
/**
 * Build the Andrea tracker card.
 *
 *   node tools/cards/build-andrea-tracker-template.mjs
 *
 * The stock bento-style tracker is a dating-sim card. Rendering it for
 * Andrea produced an empty dashed box where the pregnancy/cycle pill was,
 * a change indicator that sat on top of the meter value, band labels the
 * model got wrong (ap=40 came back as "Strangers"), and four stacked rows
 * of candy-coloured bars that read as a romance dashboard rather than a
 * tracker for a woman buying drugs on a landing.
 *
 * So the card is written from scratch instead. The model supplies eight
 * values and nothing else; every word on the card is computed here:
 *
 *   ap dp tp cp      the four meters
 *   place distance   where she is
 *   last_react       her read on what you just said
 *   internal_thought what she is actually thinking
 *
 * The prompt and the field list live in andrea/tracker-prompt.json, which
 * tools/chat/configure-andrea-extensions.mjs also reads, so the template on
 * disk and the settings can never drift apart.
 */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..', '..');
const EXT = path.join(ROOT, 'SillyTavern', 'data', 'default-user', 'extensions', 'SillyTavern-SimTracker');
const TPL_DIR = path.join(EXT, 'tracker-card-templates');

const { sysPrompt, fields } = JSON.parse(
    fs.readFileSync(path.join(HERE, 'andrea', 'tracker-prompt.json'), 'utf8'),
);

// --- bands, computed from the number rather than asked of the model ---------
const AP_BAND = '{{#if (gt stats.affection 180)}}Devoted Partner{{else if (gt stats.affection 150)}}Committed Relationship{{else if (gt stats.affection 120)}}Going Steady{{else if (gt stats.affection 90)}}Romantic Interest{{else if (gt stats.affection 60)}}Good Friends{{else if (gt stats.affection 30)}}Acquaintances{{else}}Strangers{{/if}}';
const DP_BAND = '{{#if (gt stats.desire 125)}}Pliable in the lustful hunger{{else if (gt stats.desire 100)}}A desperate need presents{{else if (gt stats.desire 75)}}Body\'s burning up!{{else if (gt stats.desire 50)}}Starting to feel warm{{else if (gt stats.desire 25)}}A smoldering flame builds{{else}}Not feeling the heat{{/if}}';
const TP_BAND = '{{#if (gt stats.trust 110)}}Blindly trusts you{{else if (gt stats.trust 80)}}Willing to deal{{else if (gt stats.trust 50)}}Open, cautiously{{else if (gt stats.trust 25)}}Wary{{else}}Trusts nothing{{/if}}';
const CP_BAND = '{{#if (gt stats.contempt 100)}}Despises you{{else if (gt stats.contempt 75)}}Holds you in contempt{{else if (gt stats.contempt 50)}}Contemptuous{{else if (gt stats.contempt 25)}}Irritated by you{{else}}Tolerable{{/if}}';

const METERS = [
    { key: 'affection', name: 'Affection', band: AP_BAND, scale: 2, cls: 'aff' },
    { key: 'desire', name: 'Desire', band: DP_BAND, scale: 1.5, cls: 'des' },
    { key: 'trust', name: 'Trust', band: TP_BAND, scale: 1.5, cls: 'tru' },
    // A rise in contempt is bad news, so its delta is coloured the other way
    // round from the other three.
    { key: 'contempt', name: 'Contempt', band: CP_BAND, scale: 1.5, cls: 'con', invert: true },
];

const meterHtml = METERS.map((m) => {
    const good = m.invert ? 'bad' : 'good';
    const bad = m.invert ? 'good' : 'bad';
    return `
                <div class="at-tile">
                    <div class="at-tile-head">
                        <span class="at-label">${m.name}</span>
                        <span class="at-val">{{stats.${m.key}}}</span>
                        {{#if stats.${m.key}Change}}<span class="at-delta {{#if (gt stats.${m.key}Change 0)}}${good}{{else}}${bad}{{/if}}">{{#if (gt stats.${m.key}Change 0)}}+{{/if}}{{stats.${m.key}Change}}</span>{{/if}}
                    </div>
                    <div class="at-band">${m.band}</div>
                    <div class="at-track"><div class="at-fill ${m.cls}" style="width: {{divide stats.${m.key} ${m.scale}}}%"></div></div>
                </div>`;
}).join('');

const html = `<!-- ANDREA TRACKER -->
<style>
    .at-card {
        width: 100%;
        max-width: 430px;
        margin: 0 auto;
        background: rgba(0, 0, 0, 0.32);
        border: 1px solid rgba(255, 255, 255, 0.09);
        border-radius: 14px;
        padding: 14px 16px 12px;
        font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
        color: #e6e1da;
        box-sizing: border-box;
    }
    .at-card *, .at-card *::before, .at-card *::after { box-sizing: border-box; }

    .at-head { display: flex; align-items: center; gap: 11px; }
    .at-avatar {
        width: 30px; height: 30px; flex: 0 0 30px;
        border-radius: 50%;
        background: rgba(255, 255, 255, 0.07);
        border: 1px solid rgba(255, 255, 255, 0.12);
        display: flex; align-items: center; justify-content: center;
        font-size: 0.85rem; font-weight: 700; color: #cfc6ba;
    }
    .at-id { flex: 1; min-width: 0; }
    .at-name { font-size: 0.92rem; font-weight: 700; letter-spacing: -0.2px; }
    .at-pos {
        font-size: 0.68rem; color: #9a9186; margin-top: 1px;
        white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .at-pos b { color: #c3b9ab; font-weight: 600; }
    .at-react {
        flex: 0 0 auto; font-size: 0.6rem; font-weight: 700; letter-spacing: 0.07em;
        text-transform: uppercase; padding: 4px 8px; border-radius: 999px;
        border: 1px solid transparent;
    }
    .at-react.ok  { color: #7fc9a3; border-color: rgba(127, 201, 163, 0.3); background: rgba(127, 201, 163, 0.08); }
    .at-react.no  { color: #e08b8b; border-color: rgba(224, 139, 139, 0.3); background: rgba(224, 139, 139, 0.08); }
    .at-react.mid { color: #a8a196; border-color: rgba(255, 255, 255, 0.12); background: rgba(255, 255, 255, 0.04); }

    .at-grid {
        display: grid; grid-template-columns: 1fr 1fr; gap: 10px 16px;
        margin: 13px 0 0; padding: 12px 0 0;
        border-top: 1px solid rgba(255, 255, 255, 0.07);
    }
    .at-tile { position: relative; min-width: 0; }
    .at-tile-head { display: flex; align-items: baseline; gap: 5px; }
    .at-label {
        flex: 1; min-width: 0;
        font-size: 0.58rem; font-weight: 700; letter-spacing: 0.1em;
        text-transform: uppercase; color: #7d746a;
    }
    .at-val { font-size: 0.95rem; font-weight: 700; color: #ece6de; font-variant-numeric: tabular-nums; }
    .at-band {
        font-size: 0.63rem; color: #9a9186; margin-top: 1px;
        white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .at-track {
        height: 4px; margin-top: 7px; border-radius: 3px;
        background: rgba(255, 255, 255, 0.07); overflow: hidden;
    }
    .at-fill { height: 100%; border-radius: 3px; transition: width 0.6s ease; }
    .at-fill.aff { background: #c98a95; }
    .at-fill.des { background: #d3a05c; }
    .at-fill.tru { background: #6fa5a0; }
    .at-fill.con { background: #8f8ea8; }
    .at-delta {
        font-size: 0.6rem; font-weight: 700; font-variant-numeric: tabular-nums;
        letter-spacing: 0.01em; align-self: baseline;
    }
    .at-delta.up, .at-delta.good { color: #7fc9a3; }
    .at-delta.down, .at-delta.bad { color: #e08b8b; }

    .at-thought {
        margin-top: 13px; padding-top: 11px;
        border-top: 1px solid rgba(255, 255, 255, 0.07);
        font-size: 0.76rem; font-style: italic; line-height: 1.45; color: #b8afa4;
    }
    .at-thought::before {
        content: "she is thinking"; display: block; font-style: normal;
        font-size: 0.55rem; font-weight: 700; letter-spacing: 0.11em; text-transform: uppercase;
        color: #6f665c; margin-bottom: 4px;
    }
</style>

<div class="at-card">
    <div class="at-head">
        <div class="at-avatar">{{rawFirstLetter characterName}}</div>
        <div class="at-id">
            <div class="at-name">{{characterName}}</div>
            <div class="at-pos"><b>&#9679; {{stats.place}}</b> &middot; {{stats.distance}}m away</div>
        </div>
        <div class="at-react {{#if (eq stats.last_react 1)}}ok{{else if (eq stats.last_react 2)}}no{{else}}mid{{/if}}">
            {{#if (eq stats.last_react 1)}}Liked it{{else if (eq stats.last_react 2)}}Disapproved{{else}}Neutral{{/if}}
        </div>
    </div>

    <div class="at-grid">${meterHtml}
    </div>

    <div class="at-thought">{{stats.internal_thought}}</div>
</div>`;

const out = {
    templateName: 'Andrea - Four Meters and Position',
    templateAuthor: 'built for the Andrea card',
    templatePosition: 'BOTTOM',
    htmlTemplate: html,
    sysPrompt,
    customFields: fields,
    extSettings: {
        codeBlockIdentifier: 'sim',
        defaultBgColor: '#6a5acd',
        showThoughtBubble: true,
        hideSimBlocks: true,
        templateFile: 'andrea-tracker.html',
    },
};

fs.writeFileSync(path.join(TPL_DIR, 'andrea-tracker.json'), JSON.stringify(out, null, 2) + '\n');
fs.writeFileSync(path.join(TPL_DIR, 'andrea-tracker.html'), html);

console.log('wrote andrea-tracker.json');
console.log('  fields       :', fields.length, '->', fields.map((f) => f.key).join(' '));
console.log('  sysPrompt    :', sysPrompt.length, 'chars');
console.log('  htmlTemplate :', html.length, 'chars');
console.log('  bands        : all four computed in the template');
