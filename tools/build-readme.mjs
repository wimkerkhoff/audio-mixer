// Regenerates README.html from README.md. README.html is a build artifact — do NOT hand-edit it;
// edit README.md and run `node tools/build-readme.mjs`.
//
// Dependency-free (no npm install). Supports the Markdown subset this README uses: ATX headings
// (## get slug ids for anchors), paragraphs, **bold**, *italic*, `code`, [links](url), fenced code
// blocks, unordered/ordered lists, GitHub pipe tables, and > blockquotes.
//
// The output has a light/dark/auto theme toggle (top-right); "auto" follows prefers-color-scheme,
// and the explicit choice is persisted in localStorage.

import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const md = readFileSync(join(root, 'README.md'), 'utf8');

const LIGHT_VARS = `
    --bg: #ffffff;
    --panel: #f4f4f6;
    --border: #dcdce2;
    --fg: #1c1c20;
    --muted: #65656e;
    --link: #2563eb;
    --code-bg: #f3f3f5;
    --accent: #b9791a;
    --strong: #000;`;

const CSS = `
  :root {
    --bg: #1e1e22;
    --panel: #26262c;
    --border: #3a3a42;
    --fg: #e8e8ea;
    --muted: #9a9aa4;
    --link: #6aa9ff;
    --code-bg: #16161a;
    --accent: #f2a93b;
    --strong: #fff;
  }
  :root[data-theme="light"] {${LIGHT_VARS}
  }
  @media (prefers-color-scheme: light) {
    :root[data-theme="auto"] {${LIGHT_VARS}
    }
  }
  * { box-sizing: border-box; }
  html { scroll-behavior: smooth; }
  body {
    margin: 0;
    padding: 0;
    background: var(--bg);
    color: var(--fg);
    font: 15px/1.55 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
    -webkit-font-smoothing: antialiased;
    transition: background 0.15s, color 0.15s;
  }
  .layout {
    display: flex;
    gap: 36px;
    max-width: 1120px;
    margin: 0 auto;
    padding: 40px 32px 80px;
    align-items: flex-start;
  }
  main {
    flex: 1 1 auto;
    max-width: 820px;
    min-width: 0;
  }
  .toc {
    position: sticky;
    top: 24px;
    flex: 0 0 200px;
    width: 200px;
    max-height: calc(100vh - 48px);
    overflow-y: auto;
  }
  .toc-title {
    color: var(--muted);
    text-transform: uppercase;
    letter-spacing: 0.05em;
    font-size: 11px;
    margin: 0 0 8px 8px;
  }
  .toc ul { list-style: none; padding: 0; margin: 0; }
  .toc li { margin: 1px 0; }
  .toc a {
    display: block;
    color: var(--muted);
    text-decoration: none;
    font-size: 13px;
    padding: 3px 8px;
    border-left: 2px solid transparent;
    border-radius: 0 3px 3px 0;
  }
  .toc a:hover { color: var(--fg); background: var(--panel); }
  .toc a.toc-h3 { padding-left: 18px; font-size: 12px; }
  .toc a.active { color: var(--accent); border-left-color: var(--accent); }
  @media (max-width: 900px) {
    .toc { display: none; }
    .layout { padding: 40px 24px 80px; }
  }
  h1 { font-size: 32px; margin: 0 0 8px; }
  h2 { font-size: 22px; margin: 32px 0 10px; padding-top: 12px; border-top: 1px solid var(--border); scroll-margin-top: 16px; }
  h3 { font-size: 17px; margin: 24px 0 6px; color: var(--fg); scroll-margin-top: 16px; }
  p { margin: 8px 0 12px; }
  a { color: var(--link); }
  ul, ol { padding-left: 22px; }
  li { margin: 4px 0; }
  strong { color: var(--strong); }
  code {
    font-family: "Cascadia Code", Consolas, "JetBrains Mono", monospace;
    font-size: 13px;
    background: var(--code-bg);
    border: 1px solid var(--border);
    border-radius: 3px;
    padding: 1px 5px;
  }
  pre {
    background: var(--code-bg);
    border: 1px solid var(--border);
    border-radius: 4px;
    padding: 12px 14px;
    overflow-x: auto;
  }
  pre code {
    background: transparent;
    border: 0;
    padding: 0;
  }
  blockquote {
    margin: 12px 0;
    padding: 2px 14px;
    border-left: 3px solid var(--border);
    color: var(--muted);
  }
  table {
    width: 100%;
    border-collapse: collapse;
    margin: 12px 0;
    font-size: 14px;
  }
  th, td {
    text-align: left;
    padding: 8px 10px;
    border-bottom: 1px solid var(--border);
  }
  th { color: var(--muted); font-weight: 600; }
  .lead { color: var(--muted); font-size: 16px; }
  .toolbar-item { color: var(--accent); }
  #theme-toggle {
    position: fixed;
    top: 14px;
    right: 14px;
    z-index: 10;
    background: var(--panel);
    color: var(--fg);
    border: 1px solid var(--border);
    border-radius: 6px;
    padding: 5px 10px;
    font: 13px/1 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
    cursor: pointer;
  }
  #theme-toggle:hover { border-color: var(--accent); }`;

const HEAD_SCRIPT =
  `(function(){try{document.documentElement.dataset.theme=localStorage.getItem('theme')||'auto';}` +
  `catch(e){document.documentElement.dataset.theme='auto';}})();`;

const BODY_SCRIPT =
  `(function(){var modes=['auto','light','dark'],labels={auto:'\\u25D1 Auto',light:'\\u2600 Light',` +
  `dark:'\\u263D Dark'},btn=document.getElementById('theme-toggle');` +
  `function cur(){return document.documentElement.dataset.theme||'auto';}` +
  `function render(){btn.textContent=labels[cur()]||labels.auto;}render();` +
  `btn.addEventListener('click',function(){var n=modes[(modes.indexOf(cur())+1)%modes.length];` +
  `document.documentElement.dataset.theme=n;try{localStorage.setItem('theme',n);}catch(e){}render();});})();`;

const TOC_SCRIPT =
  `(function(){var links={};document.querySelectorAll('.toc a').forEach(function(a){` +
  `links[a.getAttribute('href').slice(1)]=a;});` +
  `var heads=document.querySelectorAll('main h2[id],main h3[id]');if(!heads.length)return;` +
  `var obs=new IntersectionObserver(function(es){es.forEach(function(e){if(e.isIntersecting){` +
  `for(var k in links)links[k].classList.remove('active');var l=links[e.target.id];` +
  `if(l)l.classList.add('active');}});},{rootMargin:'0px 0px -80% 0px'});` +
  `heads.forEach(function(h){obs.observe(h);});})();`;

const escapeHtml = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

const slug = (s) => s.toLowerCase().replace(/[^\w\s-]/g, '').trim().replace(/\s+/g, '-');

function inline(text) {
  const codes = [];
  text = text.replace(/`([^`]+)`/g, (_, c) => {
    codes.push('<code>' + escapeHtml(c) + '</code>');
    return '@@CODE' + (codes.length - 1) + '@@';
  });
  text = escapeHtml(text);
  text = text.replace(/\[([^\]]+)\]\(([^)]+)\)/g, (_, t, h) => `<a href="${h}">${t}</a>`);
  text = text.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
  text = text.replace(/(^|[^*])\*([^*\n]+)\*(?!\*)/g, '$1<em>$2</em>');
  text = text.replace(/@@CODE(\d+)@@/g, (_, i) => codes[+i]);
  return text;
}

const lines = md.replace(/\r\n/g, '\n').split('\n');
const out = [];
const toc = [];
let i = 0;
let leadUsed = false;
const isTableSep = (s) => /^\s*\|?\s*:?-{1,}:?\s*(\|\s*:?-{1,}:?\s*)+\|?\s*$/.test(s);
const splitRow = (s) => s.replace(/^\s*\|/, '').replace(/\|\s*$/, '').split('|').map((c) => c.trim());

while (i < lines.length) {
  const line = lines[i];
  if (line.trim() === '') { i++; continue; }

  const fence = line.match(/^```(\w*)\s*$/);
  if (fence) {
    i++;
    const buf = [];
    while (i < lines.length && !/^```\s*$/.test(lines[i])) { buf.push(lines[i]); i++; }
    i++;
    out.push('<pre><code>' + escapeHtml(buf.join('\n')) + '</code></pre>');
    continue;
  }

  const h = line.match(/^(#{1,6})\s+(.*)$/);
  if (h) {
    const level = h[1].length;
    const text = h[2].trim();
    if (level === 1) {
      out.push(`<h1>${inline(text)}</h1>`);
    } else if (level === 2 || level === 3) {
      const id = slug(text);
      toc.push({ level, id, label: inline(text) });
      out.push(`<h${level} id="${id}">${inline(text)}</h${level}>`);
    } else {
      out.push(`<h${level}>${inline(text)}</h${level}>`);
    }
    i++;
    continue;
  }

  if (line.includes('|') && i + 1 < lines.length && isTableSep(lines[i + 1])) {
    const header = splitRow(line);
    i += 2;
    const rows = [];
    while (i < lines.length && lines[i].includes('|') && lines[i].trim() !== '') {
      rows.push(splitRow(lines[i]));
      i++;
    }
    let t = '<table>\n  <thead><tr>' + header.map((c) => `<th>${inline(c)}</th>`).join('') + '</tr></thead>\n  <tbody>\n';
    for (const r of rows) t += '    <tr>' + r.map((c) => `<td>${inline(c)}</td>`).join('') + '</tr>\n';
    t += '  </tbody>\n</table>';
    out.push(t);
    continue;
  }

  if (/^>\s?/.test(line)) {
    const buf = [];
    while (i < lines.length && /^>\s?/.test(lines[i])) { buf.push(lines[i].replace(/^>\s?/, '')); i++; }
    out.push('<blockquote><p>' + inline(buf.join(' ')) + '</p></blockquote>');
    continue;
  }

  if (/^[-*]\s+/.test(line)) {
    const items = [];
    while (i < lines.length && /^[-*]\s+/.test(lines[i])) { items.push(lines[i].replace(/^[-*]\s+/, '')); i++; }
    out.push('<ul>\n' + items.map((it) => `  <li>${inline(it)}</li>`).join('\n') + '\n</ul>');
    continue;
  }

  if (/^\d+\.\s+/.test(line)) {
    const items = [];
    while (i < lines.length && /^\d+\.\s+/.test(lines[i])) { items.push(lines[i].replace(/^\d+\.\s+/, '')); i++; }
    out.push('<ol>\n' + items.map((it) => `  <li>${inline(it)}</li>`).join('\n') + '\n</ol>');
    continue;
  }

  const buf = [];
  while (
    i < lines.length &&
    lines[i].trim() !== '' &&
    !/^(#{1,6})\s/.test(lines[i]) &&
    !/^```/.test(lines[i]) &&
    !/^[-*]\s+/.test(lines[i]) &&
    !/^\d+\.\s+/.test(lines[i]) &&
    !/^>\s?/.test(lines[i]) &&
    !(lines[i].includes('|') && i + 1 < lines.length && isTableSep(lines[i + 1]))
  ) {
    buf.push(lines[i]);
    i++;
  }
  const cls = !leadUsed ? ' class="lead"' : '';
  leadUsed = true;
  out.push(`<p${cls}>${inline(buf.join(' '))}</p>`);
}

const tocHtml =
  '<nav class="toc" aria-label="Contents">\n  <div class="toc-title">Contents</div>\n  <ul>\n' +
  toc.map((t) => `    <li><a class="toc-h${t.level}" href="#${t.id}">${t.label}</a></li>`).join('\n') +
  '\n  </ul>\n</nav>';

const html = `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>AudioMixer</title>
<style>${CSS}
</style>
<script>${HEAD_SCRIPT}</script>
</head>
<body>
<button id="theme-toggle" aria-label="Toggle color theme" title="Theme: auto / light / dark"></button>
<div class="layout">
${tocHtml}
<main>

${out.join('\n\n')}

</main>
</div>
<script>${BODY_SCRIPT}</script>
<script>${TOC_SCRIPT}</script>
</body>
</html>
`;

writeFileSync(join(root, 'README.html'), html, 'utf8');
console.log('Wrote README.html (' + out.length + ' blocks)');
