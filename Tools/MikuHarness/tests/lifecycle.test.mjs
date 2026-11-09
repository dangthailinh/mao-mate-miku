import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

test('bridge starts without network, accepts cancellation and exits on EOF', async () => {
  const directory = dirname(dirname(fileURLToPath(import.meta.url)));
  const child = spawn(process.execPath, [join(directory, 'bridge.mjs')], { cwd: directory, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  const lines = createInterface({ input: child.stdout });
  const packets = [];
  let ready;
  const readiness = new Promise(resolve => { ready = resolve; });
  lines.on('line', line => { const packet = JSON.parse(line); packets.push(packet); if (packet.type === 'ready') ready(packet); });
  const exit = new Promise((resolve, reject) => { child.on('exit', resolve); child.on('error', reject); });
  try {
    const packet = await Promise.race([readiness, new Promise((_, reject) => setTimeout(() => reject(new Error('ready timeout')), 5000).unref())]);
    assert.equal(packet.protocol, 1);
    child.stdin.write(JSON.stringify({ type: 'cancel', id: 'no-active-job' }) + '\n');
    child.stdin.end();
    const code = await Promise.race([exit, new Promise((_, reject) => setTimeout(() => reject(new Error('exit timeout')), 5000).unref())]);
    assert.equal(code, 0);
    assert.equal(packets.some(p => p.type === 'tool'), false);
  } finally { lines.close(); if (child.exitCode === null) child.kill(); }
});
