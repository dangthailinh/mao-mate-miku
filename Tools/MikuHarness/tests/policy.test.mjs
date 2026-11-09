import test from 'node:test';
import assert from 'node:assert/strict';
import { parseCredentials, selectProvider, createPatch, validateRequest, validateTool } from '../policy.mjs';

test('credentials stay paired with their provider and unrelated secrets are discarded', () => {
  const env = parseCredentials('TOKENROUTER_API_KEY="router-key"\nDEEPSEEK_API_KEY=deepseek-key\nOTHER_SECRET=hidden');
  assert.deepEqual(env, { TOKENROUTER_API_KEY: 'router-key', DEEPSEEK_API_KEY: 'deepseek-key' });
  assert.equal(selectProvider({ TOKENROUTER_API_KEY: 'router-key' }).baseURL, 'https://api.tokenrouter.com/v1');
  assert.equal(selectProvider({ DEEPSEEK_API_KEY: 'deepseek-key' }).baseURL, 'https://api.deepseek.com/v1');
  assert.throws(() => selectProvider({}), /credentials_missing/);
});

test('Miku profile disables shell tools and telemetry and uses read only sandbox', () => {
  const patch = createPatch(selectProvider({ TOKENROUTER_API_KEY: 'test' }), 'file:///miku-tools.mjs');
  for (const id of ['persistent-pwsh', 'persistent-bash', 'session-log-deepseek', 'plugin-package-inventory-deepseek']) {
    assert.equal(patch.find(row => row.id === id)?.disabled, true, id);
  }
  assert.equal(patch.find(row => row.id === 'sandbox-policy').config.mode, 'read-only');
  assert.equal(JSON.stringify(patch).includes('router-key'), false);
  assert.equal(patch.at(-1).insert[0].config.providers['miku-qwen'].compat.supportsStore, false);
  const gemini = createPatch(selectProvider({ GEMINI_API_KEY: 'test' }), 'file:///miku-tools.mjs');
  assert.equal(gemini.at(-1).insert[0].config.providers.google.api, undefined);
  assert.equal(gemini.at(-1).insert[0].config.providers.google.reasoning, 'minimal');
});

test('protocol rejects unknown messages and oversized input', () => {
  assert.throws(() => validateRequest({ type: 'shell', command: 'anything' }), /invalid/);
  assert.throws(() => validateRequest({ type: 'run', id: 'a', text: 'x'.repeat(12001) }), /invalid/);
  assert.deepEqual(validateRequest({ type: 'run', id: 'a', text: 'mở Notepad' }).type, 'run');
});

test('tool boundary accepts only catalog identifiers and bounded note data', () => {
  assert.equal(validateTool({ kind: 'open_target', target: 'notepad' }).target, 'notepad');
  for (const target of ['cmd', 'powershell', 'https://example.com', '../notepad', 'notepad.exe']) {
    assert.throws(() => validateTool({ kind: 'open_target', target }), /invalid/);
  }
  assert.throws(() => validateTool({ kind: 'save_note', title: 'a', content: 'x'.repeat(8001) }), /invalid/);
  assert.throws(() => validateTool({ kind: 'open_target', target: 'notepad', command: 'extra' }), /invalid/);
});
