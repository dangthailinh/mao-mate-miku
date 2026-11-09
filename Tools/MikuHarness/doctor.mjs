import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { homedir } from 'node:os';
import { parseCredentials } from './policy.mjs';

const credentials = {};
for (const file of ['.env', 'qwen.env', 'deepseek.env']) {
  try { Object.assign(credentials, parseCredentials(await readFile(join(homedir(), '.openclaw', file), 'utf8'))); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
}

if (process.argv.includes('--chat') && credentials.GEMINI_API_KEY) {
  for (const compatible of [true, false]) {
    const url = compatible ? 'https://generativelanguage.googleapis.com/v1beta/openai/chat/completions' : 'https://generativelanguage.googleapis.com/v1beta/models/gemini-3.6-flash:generateContent';
    const body = compatible ? { model: 'gemini-3.6-flash', messages: [{ role: 'user', content: 'Chào, trả lời một câu ngắn.' }], max_tokens: 100, stream: false }
      : { contents: [{ role: 'user', parts: [{ text: 'Chào, trả lời một câu ngắn.' }] }], generationConfig: { maxOutputTokens: 100 } };
    const response = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json',
      ...(compatible ? { Authorization: 'Bearer ' + credentials.GEMINI_API_KEY } : { 'x-goog-api-key': credentials.GEMINI_API_KEY }) },
      body: JSON.stringify(body), signal: AbortSignal.timeout(20000) });
    const responseText = await response.text();
    const sanitized = responseText.replace(/(?:sk-|AIza)[A-Za-z0-9_-]{12,}/g, '[redacted]');
    console.log(JSON.stringify({ test: compatible ? 'gemini-openai-chat' : 'gemini-native-chat', status: response.status, body: sanitized.slice(0, 1200) }));
  }
}
if (!credentials.TOKENROUTER_API_KEY) credentials.TOKENROUTER_API_KEY = credentials.QWEN_API_KEY;
if (!credentials.GEMINI_API_KEY && credentials.GEMINI_API_KEYS) credentials.GEMINI_API_KEY = credentials.GEMINI_API_KEYS.replace(/[\[\]"']/g, '').split(/[,;\s]+/).find(Boolean);

for (const route of [
  { name: 'tokenrouter', url: 'https://api.tokenrouter.com/v1/models', key: credentials.TOKENROUTER_API_KEY, header: 'Authorization' },
  { name: 'gemini', url: 'https://generativelanguage.googleapis.com/v1beta/models', key: credentials.GEMINI_API_KEY, header: 'x-goog-api-key' }
]) {
  if (!route.key) { console.log(JSON.stringify({ provider: route.name, configured: false })); continue; }
  try {
    const response = await fetch(route.url, { headers: { [route.header]: route.header === 'Authorization' ? 'Bearer ' + route.key : route.key }, signal: AbortSignal.timeout(15000) });
    const body = await response.json();
    const ids = (body.data || body.models || []).map(model => model.id || model.name).filter(Boolean);
    console.log(JSON.stringify({ provider: route.name, status: response.status, models: ids.filter(id => /qwen|gemini|free|deepseek/i.test(id)).slice(0, 80) }));
  } catch (error) { console.log(JSON.stringify({ provider: route.name, errorType: error.name })); }
}
