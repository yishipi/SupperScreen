import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', 'docs');
const chapters = fs.readdirSync(path.join(root, 'docs')).filter(name => name.endsWith('.md')).sort().map(name => `docs/${name}`);
const files = ['README.md', 'CONFIRMED-2026-11-10.md', 'PROTOCOL-IMPLEMENTED.md', 'VALIDATION.md', ...chapters];
const combined = files.map(file => `<!-- source: ${file} -->\n\n${fs.readFileSync(path.join(root, file), 'utf8').trim()}\n`).join('\n---\n\n');
fs.writeFileSync(path.join(root, 'ALL-IN-ONE.md'), combined);
const manifest = { project: 'SupperScreen', version: 'v0.22', windowsVersion: '0.22.0', date: '2026-11-13', phone: 'HONOR BVL-AN00 / Android 16', requirements: 'Single PC + phone / phone-triggered per-monitor PNG screenshots saved on Windows / compact phone todo list with completion only / PC-configured text/background colors on todo creation / authorized ConPTY PowerShell terminal / persistent Android navigation / Windows global todo popup / PowerShell and CMD script items with results and logs on USB+LAN+Bluetooth / elevated Windows agent / opt-in lock-screen volume and selected scripts / native Android terminal input with touch scrollback / manual network setup and automatic LAN and Bluetooth reconnect / independent todo and disk scrolling / single-screen dashboard / authenticated USB+LAN and Bluetooth / per-volume capacity and disk/network throughput / allowlisted desktop launch and volume/media control on USB+LAN+Bluetooth / immersive landscape', source_document_count: chapters.length, document_count: files.length + 1, status: 'Development validation', files: [...files, 'ALL-IN-ONE.md'] };
fs.writeFileSync(path.join(root, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
const missing = [];
for (const file of files) {
  const content = fs.readFileSync(path.join(root, file), 'utf8');
  for (const link of content.matchAll(/\]\(([^)]+)\)/g)) {
    const target = link[1].split('#')[0];
    if (!target || /^(?:https?:|mailto:)/.test(target)) continue;
    if (!fs.existsSync(path.resolve(root, path.dirname(file), target))) missing.push(`${file}: ${target}`);
  }
}
if (missing.length) throw new Error(`Broken document links:\n${missing.join('\n')}`);
console.log(`Generated ${chapters.length} chapters; ${files.length + 1} documents; local links valid.`);
