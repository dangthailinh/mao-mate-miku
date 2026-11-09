import test from 'node:test';
import assert from 'node:assert/strict';
import * as policy from '../policy.mjs';

test('deferred HHTech release keeps the source credentials unchanged and selects the existing provider', () => {
  const original = { HHTECH_API_KEY: 'proxy-test-key', TOKENROUTER_API_KEY: 'router-test-key' };
  const release = policy.applyDeferredProviderPolicy(original, true);
  assert.equal(policy.selectRuntimeProvider(release).provider, 'miku-qwen');
  assert.equal(original.HHTECH_API_KEY, 'proxy-test-key');
  assert.equal(policy.selectRuntimeProvider(policy.applyDeferredProviderPolicy(original, false)).provider, 'miku-hhtech');
});

test('HHTech key is accepted separately and selects only its fixed proxy endpoint', () => {
  const env = policy.parseCredentials('HHTECH_API_KEY="proxy-test-key"\nDEEPSEEK_API_KEY=official-test-key\nHHTECH_BASE_URL=https://untrusted.invalid\nOTHER_SECRET=discard');
  assert.deepEqual(env, { HHTECH_API_KEY: 'proxy-test-key', DEEPSEEK_API_KEY: 'official-test-key' });
  assert.deepEqual(policy.selectProvider(env), {
    provider: 'miku-hhtech', apiKeyEnv: 'HHTECH_API_KEY', api: 'openai-completions',
    baseURL: 'https://hhtechapi.net/v1', model: 'deepseek-v4'
  });
  assert.equal(policy.selectProvider(env, 'miku-deepseek').apiKeyEnv, 'DEEPSEEK_API_KEY');
  assert.throws(() => policy.selectProvider({ DEEPSEEK_API_KEY: 'official-test-key' }, 'miku-hhtech'), /credentials_missing/);
});

test('new HHTech credential is not bypassed by a previously cached Google provider', () => {
  const now = Date.UTC(2026, 9, 7, 9, 0, 0);
  const previous = { provider: 'google', utc: new Date(now - 1000).toISOString() };
  assert.equal(policy.selectRuntimeProvider({ HHTECH_API_KEY: 'proxy-test-key', GEMINI_API_KEY: 'google-test-key' }, '', previous, now).provider, 'miku-hhtech');
  assert.equal(policy.selectRuntimeProvider({ TOKENROUTER_API_KEY: 'router-test-key', GEMINI_API_KEY: 'google-test-key' }, '', previous, now).provider, 'google');
  assert.equal(policy.selectRuntimeProvider({ HHTECH_API_KEY: 'proxy-test-key', GEMINI_API_KEY: 'google-test-key' }, 'google', previous, now).provider, 'google');
});

test('stale, future and missing-credential provider cache entries are ignored', () => {
  const now = Date.UTC(2026, 9, 7, 9, 0, 0);
  for (const previous of [null, { provider: 'google', utc: 'invalid' },
    { provider: 'google', utc: new Date(now - 3600001).toISOString() },
    { provider: 'google', utc: new Date(now + 1000).toISOString() },
    { provider: 'unknown', utc: new Date(now).toISOString() }]) {
    assert.equal(policy.selectRuntimeProvider({ TOKENROUTER_API_KEY: 'router-test-key' }, '', previous, now).provider, 'miku-qwen');
  }
});

test('HHTech profile contains key name but not key value and retains restricted tools', () => {
  const route = policy.selectProvider({ HHTECH_API_KEY: 'proxy-test-key' });
  const patch = policy.createPatch(route, 'file:///miku-tools.mjs');
  const provider = patch.at(-1).insert[0].config.providers['miku-hhtech'];
  assert.equal(provider.baseURL, 'https://hhtechapi.net/v1');
  assert.equal(provider.compat.supportsStore, false);
  assert.equal(provider.models[0].id, 'deepseek-v4');
  assert.equal(JSON.stringify(patch).includes('proxy-test-key'), false);
  assert.equal(patch.find(row => row.id === 'persistent-pwsh').disabled, true);
  assert.equal(patch.find(row => row.id === 'sandbox-policy').config.mode, 'read-only');
});

test('internal fallback is separate from the explicitly selected provider', () => {
  const now = Date.UTC(2026, 9, 7, 9, 0, 0);
  const env = { HHTECH_API_KEY: 'proxy-test-key', GEMINI_API_KEY: 'google-test-key', TOKENROUTER_API_KEY: 'router-test-key' };
  assert.equal(policy.selectRuntimeProvider(env, '', null, now, 'google').provider, 'miku-hhtech');
  assert.equal(policy.selectRuntimeProvider({ GEMINI_API_KEY: 'google-test-key', TOKENROUTER_API_KEY: 'router-test-key' }, '', null, now, 'google').provider, 'google');
  assert.equal(policy.selectRuntimeProvider(env, 'miku-qwen', null, now, 'google').provider, 'miku-qwen');
});

test('diagnostics remove configured credentials even without standard key prefixes', () => {
  const env = { HHTECH_API_KEY: 'opaque-provider-test-token', DEEPSEEK_API_KEY: 'sk-other-test-token', GEMINI_API_KEY: 'quoted\\test\"token' };
  const raw = 'provider error opaque-provider-test-token ' + JSON.stringify(env.GEMINI_API_KEY) + ' Bearer other-value';
  const safe = policy.redactDiagnostics(raw, env);
  assert.equal(safe.includes(env.HHTECH_API_KEY), false);
  assert.equal(safe.includes(JSON.stringify(env.GEMINI_API_KEY).slice(1, -1)), false);
  assert.equal(safe.includes('other-value'), false);
  assert.equal(policy.redactDiagnostics('ordinary diagnostic', {}).includes('ordinary diagnostic'), true);
});
