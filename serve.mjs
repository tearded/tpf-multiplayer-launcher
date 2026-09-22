import http from 'node:http';
import { readFile } from 'node:fs/promises';
const files = new Map([['/', ['index.html', 'text/html']], ['/index.html', ['index.html', 'text/html']], ['/styles.css', ['styles.css', 'text/css']], ['/app.js', ['app.js', 'text/javascript']], ['/preview.js', ['preview.js', 'text/javascript']], ['/theme.js', ['public/theme.js', 'text/javascript']]]);
files.set('/images/transport-fever-2.png', ['public/images/transport-fever-2.png', 'image/png']);
files.set('/images/launcher-logo.svg', ['public/images/launcher-logo.svg', 'image/svg+xml']);
files.set('/fallback.html', ['fallback.html', 'text/html']);
files.set('/fallback.css', ['fallback.css', 'text/css']);
const port = Number(process.env.TPF2_PREVIEW_PORT || 4318);
const server = http.createServer(async (request, response) => {
  const file = files.get(new URL(request.url, 'http://127.0.0.1').pathname);
  if (!file || !['GET', 'HEAD'].includes(request.method)) { response.writeHead(404); response.end(); return; }
  try {
    const body = await readFile(new URL(file[0], import.meta.url));
    response.writeHead(200, { 'Content-Type': `${file[1]}; charset=utf-8`, 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
    response.end(request.method === 'HEAD' ? undefined : body);
  } catch { response.writeHead(500); response.end('Preview unavailable'); }
});
server.listen(port, '127.0.0.1', () => console.log(`Launcher preview: http://127.0.0.1:${port}`));
