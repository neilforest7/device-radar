import http from 'node:http';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.dirname(fileURLToPath(import.meta.url));
const captures = path.resolve(root, '../../artifacts/tui');
const files = new Map([
  ['/', ['index.html', 'text/html; charset=utf-8']],
  ['/viewer.js', ['viewer.js', 'text/javascript; charset=utf-8']],
  ['/viewer.css', ['viewer.css', 'text/css; charset=utf-8']],
  ['/vendor/xterm.js', ['node_modules/@xterm/xterm/lib/xterm.js', 'text/javascript; charset=utf-8']],
  ['/vendor/xterm.css', ['node_modules/@xterm/xterm/css/xterm.css', 'text/css; charset=utf-8']],
]);

const server = http.createServer(async (request, response) => {
  if (request.method !== 'GET' && request.method !== 'HEAD') {
    response.writeHead(405, { Allow: 'GET, HEAD' }).end();
    return;
  }

  const url = new URL(request.url, 'http://127.0.0.1:4178');
  let target;
  let contentType;
  const known = files.get(url.pathname);
  if (known) {
    target = path.join(root, known[0]);
    contentType = known[1];
  } else if (url.pathname === '/manifest.json') {
    target = path.join(captures, 'manifest.json');
    contentType = 'application/json; charset=utf-8';
  } else if (/^\/snapshots\/[a-z0-9-]+\.(ansi|txt)$/.test(url.pathname)) {
    target = path.join(captures, path.basename(url.pathname));
    contentType = 'text/plain; charset=utf-8';
  } else {
    response.writeHead(404).end('Not found');
    return;
  }

  try {
    const data = await readFile(target);
    response.writeHead(200, {
      'Content-Type': contentType,
      'Cache-Control': 'no-store',
      'X-Content-Type-Options': 'nosniff',
      'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self'; img-src 'self' data:; connect-src 'self'",
    });
    response.end(request.method === 'HEAD' ? undefined : data);
  } catch (error) {
    response.writeHead(error.code === 'ENOENT' ? 404 : 500).end(
      error.code === 'ENOENT' ? 'Snapshot not generated yet' : 'Unable to read snapshot',
    );
  }
});

server.listen(4178, '127.0.0.1', () => {
  console.log('Device Radar real TUI render: http://127.0.0.1:4178');
});
