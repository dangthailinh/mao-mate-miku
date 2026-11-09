import test from 'node:test';
import assert from 'node:assert/strict';
import { createPatch, selectProvider, validateTool, canFallback, toolBudget, taskDeadline, validateRequest } from '../policy.mjs';

test('explicit full profile communicates actual computer capabilities', () => {
  const patch = createPatch(selectProvider({ GEMINI_API_KEY: 'fake' }), 'file:///miku-tools.mjs', 'full-access');
  assert.equal(patch.find(p => p.id === 'sandbox-policy').config.mode, 'danger-full-access');
  assert.match(patch.find(p => p.id === 'system-prompt').config.personaPrefix, /run_powershell/);
  assert.equal(patch.find(p => p.id === 'persistent-pwsh').disabled, true);
});
test('computer schemas accept bounded real file and shell requests', () => {
  assert.equal(validateTool({ kind: 'read_file', path: 'demo.txt' }).path, 'demo.txt');
  assert.equal(validateTool({ kind: 'list_directory', path: '.' }).kind, 'list_directory');
  assert.equal(validateTool({ kind: 'write_file', path: 'demo.txt', content: 'demo', overwrite: false }).content, 'demo');
  assert.equal(validateTool({ kind: 'run_powershell', command: "Write-Output 'MIKU_TEST'", cwd: '' }).kind, 'run_powershell');
  assert.equal(validateTool({ kind: 'open_url', url: 'https://example.com/' }).kind, 'open_url');
});
test('computer schemas reject malformed instructions and privilege injection', () => {
  for (const request of [
    { kind: 'read_file', path: 'a\u0000b' },
    { kind: 'write_file', path: 'a', content: 'x', overwrite: 'true' },
    { kind: 'run_powershell', command: 'x'.repeat(12001), cwd: '.' },
    { kind: 'open_url', url: 'javascript:alert(1)' },
    { kind: 'run_powershell', command: 'Write-Output 1', cwd: '.', permissions: 'full-access' }
  ]) assert.throws(() => validateTool(request));
});
test('provider retry is forbidden after a tool or explicit provider selection', () => {
  assert.equal(canFallback('miku-qwen', 0, '', 0), true);
  assert.equal(canFallback('miku-qwen', 1, '', 0), false);
  assert.equal(canFallback('miku-qwen', 0, 'miku-qwen', 0), false);
  assert.equal(canFallback('miku-hhtech', 0, '', 0), false);
  assert.equal(canFallback('miku-qwen', 0, '', 1), false);
});
test('mode budgets and protocol remain bounded and fail closed', () => {
  assert.equal(toolBudget('full-access'), 32); assert.equal(toolBudget('ask'), 16); assert.equal(toolBudget('read-only'), 6);
  assert.equal(taskDeadline('full-access'), 600000);
  assert.equal(validateRequest({type:'run', id:'job', text:'test'}).permissionMode, 'read-only');
  assert.throws(() => validateRequest({type:'run', id:'job', text:'test', permissionMode:'administrator'}));
});
test('ask profile calls a tool to request approval and protocol cannot infer authority', () => {
  const profile = createPatch(selectProvider({GEMINI_API_KEY:'fake'}), 'file:///miku-tools.mjs', 'ask');
  assert.match(profile.find(p => p.id === 'system-prompt').config.personaPrefix, /HÃY GỌI CÔNG CỤ/);
  assert.match(profile.find(p => p.id === 'system-prompt').config.personaPrefix, /KHÔNG thực thi/);
  assert.equal(validateRequest({type:'run',id:'test',text:'task',requiresTools:true}).requiresTools, true);
  assert.throws(() => validateRequest({type:'run',id:'test',text:'task',requiresTools:'full'}));
});
