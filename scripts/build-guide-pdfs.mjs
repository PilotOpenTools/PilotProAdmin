import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const chrome = process.argv[2] ?? 'google-chrome';
const site = resolve('_site');
const output = join(site, 'downloads');
const guides = ['installation', 'security', 'gitlab-offline'];
const temp = mkdtempSync(join(tmpdir(), 'pilotproadmin-pdf-'));

const printCss = `
  @page { size: A4; margin: 16mm; }
  body { font: 10.5pt/1.45 Arial, sans-serif; color: #172033; }
  h1 { font-size: 25pt; }
  h2 { font-size: 16pt; }
  h3 { font-size: 12pt; }
  h1, h2, h3, h4 { break-after: avoid-page; }
  p, li { overflow-wrap: anywhere; }
  a { color: #17457a; overflow-wrap: anywhere; }
  .guide-download, .anchorjs-link { display: none; }
  table { border-collapse: collapse; width: 100%; font-size: 9.5pt; }
  th, td { border: 1px solid #c4cbd5; padding: 5px; text-align: left; vertical-align: top; overflow-wrap: anywhere; }
  th { background: #e9eef5; }
  tr { break-inside: avoid-page; }
  code { font-family: Consolas, 'Liberation Mono', monospace; font-size: .92em; }
  pre { padding: 8px; background: #eff2f6; white-space: pre-wrap; overflow-wrap: anywhere; }
  blockquote { margin: 0.8em 0; padding: 0 0.9em; border-left: 3px solid #a6b3c6; }
`;

try {
  mkdirSync(output, { recursive: true });
  for (const guide of guides) {
    const page = readFileSync(join(site, 'stack', 'Install', 'docs', `${guide}.html`), 'utf8');
    const article = page.match(/<article\b[^>]*>[\s\S]+?<\/article>/)?.[0];
    const title = page.match(/<title>([^<]+)<\/title>/)?.[1];
    if (!article || !title) throw new Error(`Missing DocFX content for ${guide}`);

    const source = join(temp, `${guide}.html`);
    const pdf = join(output, `${guide}.pdf`);
    writeFileSync(source, `<!doctype html><html lang="ru"><meta charset="utf-8">
      <base href="https://pilotopentools.github.io/PilotProAdmin/stack/Install/docs/${guide}.html">
      <title>${title}</title><style>${printCss}</style><body>${article}</body></html>`);
    const result = spawnSync(chrome, [
      '--headless', '--no-first-run', '--no-pdf-header-footer',
      `--user-data-dir=${join(temp, 'browser')}`, `--print-to-pdf=${pdf}`, pathToFileURL(source).href,
    ], { encoding: 'utf8', windowsHide: true, timeout: 60_000 });
    if (result.error || result.status !== 0 || !existsSync(pdf) ||
        !readFileSync(pdf).subarray(0, 5).equals(Buffer.from('%PDF-'))) {
      throw new Error(`Failed to render ${guide}: ${result.error ?? result.stderr}`);
    }
    console.log(pdf);
  }
} finally {
  rmSync(temp, { recursive: true, maxRetries: 5 });
}
