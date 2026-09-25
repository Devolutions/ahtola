// Exports the Unicode properties the managed FTS tokenizers pin, using Node's bundled ICU.
//
// .NET does not expose the Alphabetic derived property that Rust's char::is_alphanumeric (and
// therefore Tantivy's SimpleTokenizer) is defined over, and its canonical decomposition and case
// mapping come from whichever ICU the host happens to load (none at all in globalization-invariant
// mode). This script captures those properties once; GenerateManagedUnicodeData.cs then restricts
// them to the repertoire the pinned Unicode version assigns and emits a static C# table.
//
// Usage: node Export-UnicodeProperties.mjs <output.json>
import { writeFileSync } from "node:fs";

const output = process.argv[2];
if (!output) {
    console.error("Usage: node Export-UnicodeProperties.mjs <output.json>");
    process.exit(2);
}

const alphabetic = /^\p{Alphabetic}$/u;
const ranges = [];
const decompositions = {};
const lowercase = {};

let rangeStart = -1;
for (let cp = 0; cp <= 0x10ffff; cp++) {
    const isSurrogate = cp >= 0xd800 && cp <= 0xdfff;
    const text = isSurrogate ? "" : String.fromCodePoint(cp);
    const isAlphabetic = !isSurrogate && alphabetic.test(text);

    if (isAlphabetic && rangeStart < 0) {
        rangeStart = cp;
    } else if (!isAlphabetic && rangeStart >= 0) {
        ranges.push([rangeStart, cp - 1]);
        rangeStart = -1;
    }

    if (isSurrogate) {
        continue;
    }

    const nfd = text.normalize("NFD");
    if (nfd !== text) {
        decompositions[cp] = [...nfd].map((c) => c.codePointAt(0));
    }

    // A lone code point has no casing context, so this is the per-character full mapping that
    // Rust's char::to_lowercase (and Tantivy's LowerCaser) applies.
    const lower = text.toLowerCase();
    if (lower !== text) {
        lowercase[cp] = [...lower].map((c) => c.codePointAt(0));
    }
}

if (rangeStart >= 0) {
    ranges.push([rangeStart, 0x10ffff]);
}

writeFileSync(
    output,
    JSON.stringify({
        unicode: process.versions.unicode,
        icu: process.versions.icu,
        alphabetic: ranges,
        decompositions,
        lowercase,
    }),
);
console.log(
    `unicode ${process.versions.unicode} (icu ${process.versions.icu}): ` +
        `${ranges.length} alphabetic ranges, ${Object.keys(decompositions).length} decompositions, ` +
        `${Object.keys(lowercase).length} lowercase mappings`,
);
