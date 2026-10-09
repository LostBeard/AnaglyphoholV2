// summarize.mjs - medians (min..max, n) per build and scenario from one or more bench.mjs result files.
//   node _tools/bench/summarize.mjs <results.jsonl> [more.jsonl ...]
import fs from 'node:fs';

const rows = process.argv.slice(2).flatMap(f => fs.readFileSync(f, 'utf8').trim().split('\n').map(l => JSON.parse(l)));
const fields = ['firstPixelsMs', 'firstFrameMs', 'fps3d', 'sourceFps', 'lagFramesMedian', 'rafHz', 'validPct', 'probeMsPerFrame', 'probeMsPerSec'];
const med = a => { const s = [...a].sort((x, y) => x - y); const n = s.length; return n % 2 ? s[n >> 1] : (s[n / 2 - 1] + s[n / 2]) / 2; };
const groups = new Map();
for (const r of rows) {
    const k = `${r.scenario}|${r.build}`;
    if (!groups.has(k)) groups.set(k, []);
    groups.get(k).push(r);
}
for (const k of [...groups.keys()].sort()) {
    const g = groups.get(k);
    const [scenario, build] = k.split('|');
    const failed = g.filter(r => r.failed || (r.scenario.startsWith('image') && r.firstPixelsMs == null)).length;
    const parts = [];
    for (const f of fields) {
        const v = g.map(r => r[f]).filter(x => typeof x === 'number');
        if (!v.length) continue;
        parts.push(`${f} ${+med(v).toFixed(2)} (${Math.min(...v)}..${Math.max(...v)})`);
    }
    const extra = [...new Set(g.map(r => r.size).filter(Boolean))].join(',');
    console.log(`${scenario.padEnd(20)} ${build.padEnd(6)} n=${g.length}${failed ? ` FAILED=${failed}` : ''} ${parts.join('  ')}${extra ? `  overlay ${extra}` : ''}`);
}
