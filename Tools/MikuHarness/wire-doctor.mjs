// Opt-in transport diagnostic. Only a synthetic greeting is sent. Never print headers/credentials.
import { createServer } from 'node:http';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { randomBytes } from 'node:crypto';
import { DeepSeekHarness } from '@deepseek-ai/dsh-sdk-client';
import { parseCredentials, selectProvider, createPatch } from './policy.mjs';

const credentials = parseCredentials(await readFile(join(homedir(), '.openclaw', '.env'), 'utf8'));
const route = selectProvider(credentials, 'miku-gemini');
const nonce = randomBytes(32).toString('hex');
const server = createServer(async (request, response) => {
  if (request.url === '/tool' && request.headers.authorization === 'Bearer ' + nonce) {
    request.resume(); response.writeHead(200, { 'Content-Type': 'application/json' });
    response.end(JSON.stringify({ success: true, message: 'simulated transport check' })); return;
  }
  if (request.headers['x-miku-diagnostic'] !== nonce || request.url !== '/chat/completions' || request.method !== 'POST') { response.writeHead(403); response.end(); return; }
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  const body = Buffer.concat(chunks).toString('utf8');
  const value = JSON.parse(body);
  console.log(JSON.stringify({ requestPath: request.url, fields: Object.keys(value), model: value.model,
    max_tokens: value.max_tokens, stream_options: value.stream_options, tools: value.tools?.map(t => ({ name: t.function.name, parameters: t.function.parameters, strict: t.function.strict })),
    roles: value.messages.map(m => m.role), reasoning_effort: value.reasoning_effort }));
  const upstream = await fetch('https://generativelanguage.googleapis.com/v1beta/openai/chat/completions', {
    method: 'POST', headers: { Authorization: 'Bearer ' + credentials.GEMINI_API_KEY, 'Content-Type': 'application/json' }, body });
  console.log(JSON.stringify({ upstreamStatus: upstream.status }));
  response.writeHead(upstream.status, { 'Content-Type': upstream.headers.get('content-type') || 'application/json' });
  if (!upstream.ok) { const error = await upstream.text(); console.log(error.slice(0, 2000)); response.end(error); }
  else { for await (const chunk of upstream.body) response.write(chunk); response.end(); }
});
await new Promise(done => server.listen(0, '127.0.0.1', done));
const patch = createPatch(route, pathToFileURL(join(process.cwd(), 'miku-tools.mjs')).href);
patch.at(-1).insert[0].config.providers[route.provider].baseURL = 'http://127.0.0.1:' + server.address().port;
patch.at(-1).insert[0].config.providers[route.provider].headers = { 'x-miku-diagnostic': nonce };
await mkdir('.runtime', { recursive: true });
await writeFile('.runtime/wire-profile.json', JSON.stringify(patch));
const runtime = new DeepSeekHarness({ profile: 'sdk-minimal', patches: ['.runtime/wire-profile.json'], dshHome: '.runtime/wire-home',
  processCwd: process.cwd(), provider: route.provider, model: route.model, maxTokens: 1024,
  env: { ...process.env, GEMINI_API_KEY: credentials.GEMINI_API_KEY, MIKU_TOOL_ENDPOINT: 'http://127.0.0.1:' + server.address().port + '/tool', MIKU_TOOL_NONCE: nonce }, requestTimeoutMs: 40000 });
try {
  const result = await runtime.run(process.argv.includes('--tools') ? 'Mở Notepad giúp tôi, dùng công cụ open_target.' : 'Chào Miku, trả lời một câu ngắn.');
  console.log(JSON.stringify({ response: result.finalResponse, end: result.events.filter(e => e.type === 'turn/end').map(e => e.data.reason) }));
} finally { await runtime.close(); server.closeAllConnections(); server.close(); }
