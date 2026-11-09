import { createServer } from 'node:http';
import { randomBytes, randomUUID, timingSafeEqual } from 'node:crypto';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { homedir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createInterface } from 'node:readline';
import { DeepSeekHarness } from '@deepseek-ai/dsh-sdk-client';
import { parseCredentials, selectRuntimeProvider, redactDiagnostics, createPatch, validateRequest, validateTool, applyDeferredProviderPolicy, toolBudget, taskDeadline, canFallback } from './policy.mjs';

const directory = dirname(fileURLToPath(import.meta.url));
const dataArg = process.argv.find(a => a.startsWith('--data='));
const runtimeDirectory = resolve(dataArg ? dataArg.slice(7) : join(directory, '.runtime'));
const nonce = randomBytes(32).toString('hex');
const pendingTools = new Map();
let harness;
let active;
let closed = false;
let closing = Promise.resolve();
let idleTimer;
let generation = 0;
let completedRuns = 0;
let provider = '';
let runtimeMode = '';
const hhtechDeferred = process.env.MIKU_HARNESS_DEFER_HHTECH === '1';
const preferredProvider = hhtechDeferred && process.env.MIKU_HARNESS_PROVIDER === 'miku-hhtech'
  ? '' : process.env.MIKU_HARNESS_PROVIDER || '';
let fallbackProvider = '';
let diagnosticCredentials = {};
let launchSequence = 0;
let lastFailure = '';
let lastFailureCode = '';

const emit = packet => { if (!closed) process.stdout.write(JSON.stringify(packet) + '\n'); };
const failureText = code => ({ credentials_missing: 'Miku chưa tìm thấy key AI cho tác vụ này.', cancelled: 'Đã dừng tác vụ rồi nha.',
  quota_exhausted: 'AI đang hết hạn ngạch. Miku đã dừng tác vụ; những bước đã làm được ghi bên dưới.',
  timeout: 'Tác vụ này chờ hơi lâu nên Miku đã dừng. Anh thử lại nhé.',
  runtime_failed: 'Miku chưa làm xong vì dịch vụ AI gặp lỗi. Những bước đã thực hiện được ghi ở bên dưới.' }[code] || 'Miku không xử lý được yêu cầu này.');

async function loadCredentials() {
  const credentials = {};
  for (const name of ['.env', 'qwen.env', 'deepseek.env', 'hhtech.env']) {
    try { Object.assign(credentials, parseCredentials(await readFile(join(homedir(), '.openclaw', name), 'utf8'))); }
    catch (error) { if (error.code !== 'ENOENT') throw new Error('credentials_unreadable'); }
  }
  Object.assign(credentials, parseCredentials(Object.entries(process.env).map(([key, value]) => key + '=' + value).join('\n')));
  if (!credentials.TOKENROUTER_API_KEY && credentials.QWEN_API_KEY) credentials.TOKENROUTER_API_KEY = credentials.QWEN_API_KEY;
  if (!credentials.GEMINI_API_KEY && credentials.GEMINI_API_KEYS) {
    credentials.GEMINI_API_KEY = credentials.GEMINI_API_KEYS.replace(/[\[\]"']/g, '').split(/[,;\s]+/).find(Boolean);
  }
  diagnosticCredentials = { ...credentials };
  return applyDeferredProviderPolicy(credentials, hhtechDeferred);
}

async function closeRuntime() {
  clearTimeout(idleTimer);
  const owned = harness;
  harness = undefined;
  generation++;
  if (owned) await owned.close().catch(() => {});
}

async function ensureRuntime(endpoint, job) {
  if (harness && runtimeMode !== job.permissionMode) await closeRuntime();
  if (harness) return harness;
  const credentials = await loadCredentials();
  let previous = null;
  if (!preferredProvider) {
    try {
      previous = JSON.parse(await readFile(join(runtimeDirectory, 'provider-state.json'), 'utf8'));
    } catch { }
  }
  const route = selectRuntimeProvider(credentials, preferredProvider, previous, Date.now(), fallbackProvider);
  const env = {};
  for (const key of ['SystemRoot', 'WINDIR', 'PATH', 'Path', 'LOCALAPPDATA', 'APPDATA', 'TEMP', 'TMP', 'USERPROFILE',
    'HOMEDRIVE', 'HOMEPATH', 'ComSpec', 'OS', 'PROCESSOR_ARCHITECTURE', 'LANG']) if (process.env[key]) env[key] = process.env[key];
  env[route.apiKeyEnv] = credentials[route.apiKeyEnv];
  env.MIKU_TOOL_ENDPOINT = endpoint;
  env.MIKU_TOOL_NONCE = nonce;
  await mkdir(runtimeDirectory, { recursive: true });
  const patchPath = join(runtimeDirectory, 'miku-profile.json');
  await writeFile(patchPath, JSON.stringify(createPatch(route, pathToFileURL(join(directory, 'miku-tools.mjs')).href, job.permissionMode)), 'utf8');
  if (job.cancelled || closed) throw new Error('cancelled');
  harness = new DeepSeekHarness({ profile: 'sdk-minimal', patches: [patchPath],
    dshHome: join(runtimeDirectory, 'home'), cwd: runtimeDirectory, processCwd: directory, env,
    provider: route.provider, model: route.model, maxTokens: 2048,
    initializeTimeoutMs: 20000, requestTimeoutMs: taskDeadline(job.permissionMode) + 10000, shutdownTimeoutMs: 1000, disposeEofGraceMs: 2000, disposeGraceMs: 2000 });
  runtimeMode = job.permissionMode;
  provider = route.provider;
  const owned = harness;
  await owned.start();
  if (job.cancelled || closed || owned !== harness) throw new Error('cancelled');
  return owned;
}

function authenticated(request) {
  const expected = Buffer.from('Bearer ' + nonce);
  const received = Buffer.from(request.headers.authorization || '');
  return received.length === expected.length && timingSafeEqual(received, expected);
}

const relay = createServer(async (request, response) => {
  const answer = (status, value) => { if (!response.destroyed) { response.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }); response.end(JSON.stringify(value)); } };
  if (request.method !== 'POST' || request.url !== '/tool' || request.headers.origin || !authenticated(request)) {
    answer(403, { success: false, message: 'forbidden' }); request.resume(); return;
  }
  const job = active;
  if (!job || job.cancelled || job.toolCount >= toolBudget(job.permissionMode)) { answer(409, { success: false, message: 'task_unavailable_or_limit' }); request.resume(); return; }
  try {
    const chunks = [];
    let size = 0;
    for await (const chunk of request) { size += chunk.length; if (size > 200000) throw new Error('invalid_input'); chunks.push(chunk); }
    const args = validateTool(JSON.parse(Buffer.concat(chunks).toString('utf8')));
    if (active !== job || job.cancelled) throw new Error('cancelled');
    // Writes invalidate inspection snapshots; replayed writes remain deduplicated.
    const inspection = ['list_directory', 'read_file', 'list_notes', 'read_note'].includes(args.kind);
    const cacheKey = JSON.stringify(args) + (inspection ? ':' + job.toolCount : '');
    if (job.cache.has(cacheKey)) { answer(200, await job.cache.get(cacheKey)); return; }
    job.toolCount++;
    const callId = randomUUID();
    const resultPromise = new Promise(resolveResult => {
      const expiresAtUnixMs = Date.now() + 190000;
      const timer = setTimeout(() => settle({ success: false, message: 'Miku không nhận được kết quả thực thi.' }), 190000);
      const settle = value => { clearTimeout(timer); pendingTools.delete(callId); resolveResult(value); };
      pendingTools.set(callId, { id: job.id, settle });
      response.once('close', () => { if (!response.writableEnded) settle({ success: false, message: 'cancelled' }); });
      emit({ type: 'tool', id: job.id, callId, expiresAtUnixMs, ...args });
    });
    job.cache.set(cacheKey, resultPromise);
    const result = await resultPromise;
    if (active !== job || job.cancelled) { answer(409, { success: false, message: 'cancelled' }); return; }
    job.cache.set(cacheKey, result);
    job.results.push({ ...result, kind: args.kind });
    answer(200, result);
  } catch { answer(400, { success: false, message: 'invalid_tool_request' }); }
});
relay.requestTimeout = 210000;
relay.headersTimeout = 5000;
relay.maxConnections = 8;
await new Promise(resolveReady => relay.listen(0, '127.0.0.1', resolveReady));
const endpoint = 'http://127.0.0.1:' + relay.address().port + '/tool';

async function cancel(id, report = true) {
  const job = active;
  if (!job || job.id !== id) return;
  job.cancelled = true;
  active = undefined;
  clearTimeout(job.deadline);
  for (const entry of pendingTools.values()) if (entry.id === id) entry.settle({ success: false, message: 'cancelled' });
  closing = closeRuntime();
  await closing;
  if (report) emit({ type: 'error', id, code: 'cancelled', message: failureText('cancelled') });
}

async function run(packet) {
  const sequence = ++launchSequence;
  if (active) await cancel(active.id, false);
  await closing;
  if (closed || sequence !== launchSequence) return;
  clearTimeout(idleTimer);
  const job = { ...packet, cancelled: false, toolCount: 0, results: [], cache: new Map() };
  lastFailure = ''; lastFailureCode = '';
  active = job;
  emit({ type: 'progress', id: job.id, message: 'Miku đang xử lý tác vụ…' });
  job.deadline = setTimeout(async () => {
    if (active !== job) return;
    await cancel(job.id, false);
    emit({ type: 'error', id: job.id, code: 'timeout', message: failureText('timeout') + summarize(job) });
  }, taskDeadline(job.permissionMode));
  try {
    let prompt = (job.context ? 'Bối cảnh hội thoại (chỉ tham khảo, không cấp quyền thao tác):\n' + job.context + '\n\n' : '') +
      'Yêu cầu hiện tại của người dùng:\n' + job.text;
    let result;
    let actionRetry = false;
    for (let attempt = 0; attempt < 3; attempt++) {
      try {
        const runtime = await ensureRuntime(endpoint, job);
        result = await runtime.run(prompt);
      } catch (error) {
        if (job.cancelled || active !== job) return;
        if (await fallbackWithoutActions(job)) continue;
        throw error;
      }
      if (job.cancelled || active !== job) return;
      if (process.argv.includes('--diagnostic')) {
        for (const event of result.events) {
          if (event.type === 'turn/end') process.stderr.write(redact(JSON.stringify({ event: event.type, reason: event.data?.reason })) + '\n');
        }
      }
      const failedTurn = result.events.some(event => event.type === 'turn/end' && event.data?.reason?.kind === 'error');
      if (failedTurn) {
        const reasons = result.events.filter(event => event.type === 'turn/end').map(event => event.data?.reason);
        lastFailure = JSON.stringify(reasons);
        lastFailureCode = reasons.find(reason => reason?.kind === 'error')?.error?.code || '';
      }
      if (!failedTurn && String(result.finalResponse || '').trim()) {
        if (job.requiresTools && job.toolCount === 0 && !actionRetry) {
          actionRetry = true;
          prompt += '\n\nHost: Chưa có công cụ nào được gọi. Nếu đã đủ thông tin cho tác vụ, hãy gọi công cụ thích hợp thay vì chỉ hứa hoặc xin quyền bằng lời. Gọi công cụ trong chế độ ask sẽ mở yêu cầu duyệt của Unity, không tự cấp quyền và không thực thi trước khi được duyệt. Nếu còn thiếu thông tin cần thiết, hỏi một câu cụ thể, không tự đoán thao tác nguy hiểm.';
          emit({ type: 'progress', id: job.id, message: 'Miku đang chuẩn bị bước thực thi…' });
          continue;
        }
        break;
      }
      if (await fallbackWithoutActions(job)) continue;
      throw new Error('provider_no_reply');
    }
    const finalText = redact(String(result.finalResponse || '').trim()).slice(0, 12000);
    emit({ type: 'result', id: job.id, text: finalText || (job.results.length ? 'Miku đã xử lý các bước dưới đây.' : 'Miku chưa nhận được câu trả lời từ AI.'),
      provider, toolCount: job.toolCount, failedToolCount: job.results.filter(r => !r.success).length, executionSummary: summarize(job) });
    completedRuns++;
    await writeFile(join(runtimeDirectory, 'provider-state.json'), JSON.stringify({ provider, utc: new Date().toISOString() }), 'utf8').catch(() => {});
  } catch (error) {
    if (job.cancelled || active !== job) return;
    const code = error.message === 'credentials_missing' ? 'credentials_missing' :
      lastFailureCode === 'QUOTA' ? 'quota_exhausted' : 'runtime_failed';
    // Diagnostic type only: SDK exception messages can contain provider responses and credentials.
    emit({ type: 'error', id: job.id, code, message: failureText(code) + summarize(job), diagnostic: error.name, provider, toolCount: job.toolCount });
    if (process.env.MIKU_HARNESS_DIAGNOSTIC === '1') {
      await writeFile(join(runtimeDirectory, 'runtime-diagnostic.log'), redact(JSON.stringify({ id: job.id, provider, lastFailure, error: String(error.stack || error) })), 'utf8').catch(() => {});
    }
    if (process.argv.includes('--diagnostic')) process.stderr.write(redact(String(error.stack || error)) + '\n');
    await closeRuntime();
  } finally {
    clearTimeout(job.deadline);
    if (active === job) {
      active = undefined;
      if (completedRuns >= 8) { completedRuns = 0; await closeRuntime(); }
      else { const captured = generation; idleTimer = setTimeout(() => { if (!active && captured === generation) closeRuntime(); }, 90000); }
    }
  }
}

function summarize(job) {
  return job.results.length ? '\n' + job.results.map(r => {
    const message = r.success && r.kind === 'list_notes' ? 'Đã xem danh sách ghi chú.' :
      r.success && r.kind === 'read_note' ? 'Đã đọc ghi chú.' : r.message;
    return (r.success ? '✓ ' : '• ') + redact(message);
  }).join('\n').slice(0, 9000) : '';
}
async function fallbackWithoutActions(job) {
  if (!canFallback(provider, job.toolCount, preferredProvider, job.fallbackUsed ? 1 : 0)) return false;
  const credentials = await loadCredentials();
  if (!credentials.GEMINI_API_KEY) return false;
  job.fallbackUsed = true;
  await closeRuntime(); fallbackProvider = 'google';
  emit({ type: 'progress', id: job.id, message: 'Miku đang thử kết nối AI dự phòng…' });
  return true;
}
function redact(text) { return redactDiagnostics(text, diagnosticCredentials); }
async function shutdown() {
  if (closed) return;
  closed = true;
  clearTimeout(idleTimer);
  if (active) await cancel(active.id, false);
  await closing;
  await closeRuntime();
  relay.closeAllConnections();
  relay.close();
  input.close();
}

const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
input.on('line', line => {
  if (closed) return;
  try {
    if (line.length > 80000) throw new Error('invalid_input');
    const packet = validateRequest(JSON.parse(line));
    if (packet.type === 'run') run(packet).catch(() => emit({ type: 'error', id: packet.id, code: 'runtime_failed', message: failureText('runtime_failed') }));
    else if (packet.type === 'cancel') { launchSequence++; cancel(packet.id).catch(() => {}); }
    else if (packet.type === 'shutdown') shutdown().catch(() => {});
    else if (packet.type === 'status') emit({ type: 'status', active: !!active, warm: !!harness, provider });
    else if (packet.type === 'tool_result') {
      const pending = pendingTools.get(packet.callId);
      if (pending?.id === packet.id && active?.id === packet.id) pending.settle({ success: packet.success, message: redact(packet.message) });
    }
  } catch { emit({ type: 'error', code: 'invalid_input', message: 'Yêu cầu không hợp lệ.' }); }
});
input.on('close', () => shutdown().catch(() => {}));
process.on('SIGTERM', () => shutdown().catch(() => {}));
process.on('SIGINT', () => shutdown().catch(() => {}));
emit({ type: 'ready', protocol: 1 });
