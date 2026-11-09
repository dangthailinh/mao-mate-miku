import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const directory = dirname(fileURLToPath(import.meta.url));
const bridgeArg = process.argv.find(a => a.startsWith('--bridge='));
const dataArg = process.argv.find(a => a.startsWith('--data='));
const started = Date.now();
const child = spawn(process.execPath, [bridgeArg ? bridgeArg.slice(9) : join(directory, 'bridge.mjs'),
  ...(dataArg ? [dataArg] : []), ...(process.argv.includes('--diagnostic') ? ['--diagnostic'] : [])],
  { cwd: directory, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
const lines = createInterface({ input: child.stdout });
const send = value => child.stdin.write(JSON.stringify(value) + '\n');
const deadline = setTimeout(() => { send({ type: 'shutdown' }); process.exitCode = 1; }, 105000);
let requested = false;
let finished = false;
child.stderr.on('data', chunk => process.stderr.write(chunk));
lines.on('line', line => {
  const packet = JSON.parse(line);
  if (packet.type === 'ready' && !requested) {
    requested = true;
    send({ type: 'run', id: 'smoke-chat', text: process.argv.includes('--tools') ? 'Mở Notepad và Calculator cho tôi. Dùng công cụ cho từng ứng dụng.' : 'Chào Miku, trả lời đúng một câu ngắn bằng tiếng Việt.' });
  } else if (packet.type === 'tool') {
    // This independent SDK test checks transport only. Windows player smoke executes real actions.
    console.log(JSON.stringify({ type: 'tool_transport', kind: packet.kind, target: packet.target }));
    send({ type: 'tool_result', id: packet.id, callId: packet.callId, success: true, message: 'Transport smoke only, simulated opening of ' + packet.target });
  } else if (packet.type === 'result' || packet.type === 'error') {
    console.log(JSON.stringify({ ...packet, elapsedMs: Date.now() - started }));
    process.exitCode = packet.type === 'result' ? 0 : 1;
    finished = true;
    clearTimeout(deadline);
    send({ type: 'shutdown' });
    child.stdin.end();
  }
});
child.on('exit', (code) => { clearTimeout(deadline); if (!finished || code !== 0) process.exitCode = 1; });
