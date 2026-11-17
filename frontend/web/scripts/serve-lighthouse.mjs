import { createReadStream } from 'node:fs';
import { stat } from 'node:fs/promises';
import { createServer } from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { pipeline } from 'node:stream/promises';

const root = fileURLToPath(new URL('../dist/lighthouse/browser/', import.meta.url));
const index = path.join(root, 'index.html');
const types = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8', '.json': 'application/json', '.webmanifest': 'application/manifest+json',
  '.png': 'image/png', '.svg': 'image/svg+xml', '.ico': 'image/x-icon', '.woff2': 'font/woff2',
};

async function findFile(file) {
  try {
    const info = await stat(file);
    return info.isFile() ? info : undefined;
  } catch (error) {
    if (error.code !== 'ENOENT' && error.code !== 'ENOTDIR') throw error;
  }
}

await stat(index);
createServer(async (request, response) => {
  if (request.method !== 'GET' && request.method !== 'HEAD') {
    response.writeHead(405, { Allow: 'GET, HEAD' }).end();
    return;
  }

  try {
    const pathname = decodeURIComponent(new URL(request.url, 'http://localhost:4300').pathname);
    let file = path.resolve(root, '.' + pathname);
    const relative = path.relative(root, file);
    if (relative === '..' || relative.startsWith('..' + path.sep) || path.isAbsolute(relative) ||
        relative.toLowerCase() === 'web.config') {
      response.writeHead(404).end();
      return;
    }

    let info = await findFile(file);
    if (!info && request.headers.accept?.includes('text/html')) {
      file = index;
      info = await findFile(file);
    }
    if (!info) {
      response.writeHead(404).end();
      return;
    }
    response.writeHead(200, {
      'Content-Type': types[path.extname(file).toLowerCase()] || 'application/octet-stream',
      'Content-Length': info.size,
      'X-Content-Type-Options': 'nosniff',
    });
    if (request.method === 'HEAD') response.end();
    else await pipeline(createReadStream(file), response);
  } catch {
    if (!response.headersSent) response.writeHead(400).end();
    else response.destroy();
  }
}).listen(4300, 'localhost', () => console.log('Lighthouse preview at http://localhost:4300'));
