import { readFile, readdir, access } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

// Check emitted files, rather than trusting the environment source alone: a
// development build can otherwise be copied to IIS and call the visitor's PC.
const root = fileURLToPath(new URL('../', import.meta.url));
const output = path.join(root, 'dist/web/browser');
try {
  const source = await readFile(path.join(root, 'src/environments/environment.ts'), 'utf8');
  const configuredApi = source.match(/\bapiBaseUrl\s*:\s*['"]([^'"]+)['"]/)?.[1];
  if (!configuredApi) throw new Error('Production apiBaseUrl was not found.');
  const api = new URL(configuredApi);
  if (api.protocol !== 'https:' || /^(localhost|127\.\d+\.\d+\.\d+|\[::1\])$/i.test(api.hostname)) {
    throw new Error('Production apiBaseUrl must be a public HTTPS API URL.');
  }
  await access(path.join(output, 'index.html'));
  await access(path.join(output, 'web.config'));
  await access(path.join(output, 'ngsw.json'));
  const scripts = (await readdir(output)).filter((file) => file.endsWith('.js'));
  let foundApi = false;
  for (const file of scripts) {
    const content = await readFile(path.join(output, file), 'utf8');
    if (/(?:https?|wss?):\/\/(?:localhost|127\.0\.0\.1|\[::1\]):5088\b/i.test(content)) {
      throw new Error(`${file} contains the local development API. Rebuild with the production configuration.`);
    }
    if (content.includes(configuredApi)) foundApi = true;
  }
  if (!foundApi) throw new Error(`Built JavaScript does not contain the configured API URL: ${configuredApi}`);
  console.log(`Production build verified: API ${configuredApi}; IIS routing and service worker files present.`);
} catch (error) {
  console.error(`Production verification failed: ${error.message}`);
  process.exitCode = 1;
}
