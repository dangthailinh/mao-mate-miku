import { defineTool } from '@deepseek-ai/dsh-tools';
import { TARGETS, validateTool } from './policy.mjs';

export const name = 'miku-tools';
export const inject = ['tools'];

export function apply(ctx) {
  const definitions = [
    { kind: 'open_target', description: 'Open an application or website explicitly requested by the user. Supported targets: ' + TARGETS.join(', ') + '. Never open an unrequested target.',
      parameters: { target: { type: 'string', required: true } } },
    { kind: 'save_note', description: 'Save a new local Miku note only when the user asks to write/save a note. No arbitrary file access. Supply a short title and the requested content.',
      parameters: { title: { type: 'string', required: true }, content: { type: 'string', required: true } } },
    { kind: 'list_notes', description: 'List up to 20 existing local Miku notes when requested. Returns their identifiers and titles.', parameters: {} },
    { kind: 'read_note', description: 'Read one local Miku note by its identifier from list_notes. Only read notes the user asks about.',
      parameters: { noteId: { type: 'string', required: true } } },
    { kind: 'list_directory', description: 'List up to 100 entries in a directory. Paths are absolute or relative to the supplied workspace.', parameters: { path: { type: 'string', required: true } } },
    { kind: 'read_file', description: 'Read a UTF-8 text file, maximum 64 KiB. Paths are absolute or workspace-relative.', parameters: { path: { type: 'string', required: true } } },
    { kind: 'write_file', description: 'Write UTF-8 text. Set overwrite=true only when editing an existing file. The host makes a backup before overwrite. Requires user permission.',
      parameters: { path: { type: 'string', required: true }, content: { type: 'string', required: true }, overwrite: { type: 'boolean', required: true } } },
    { kind: 'run_powershell', description: 'Execute a PowerShell command for the current user task, under the current Windows account. Requires full access or exact approval. Output is bounded. Do not request administrator elevation.',
      parameters: { command: { type: 'string', required: true }, cwd: { type: 'string' }, timeoutSeconds: { type: 'number' } } },
    { kind: 'open_url', description: 'Open an HTTP or HTTPS URL in the default browser for the current task. Requires user permission.', parameters: { url: { type: 'string', required: true } } }
  ];
  for (const definition of definitions) {
    ctx.tools.register(defineTool({ name: definition.kind, description: definition.description, parameters: definition.parameters,
      output: { schema: { type: 'string' }, render: (_args, value) => [{ type: 'text', text: value }] },
      async execute(args, exec) {
        const packet = validateTool({ ...args, kind: definition.kind });
        const endpoint = new URL(process.env.MIKU_TOOL_ENDPOINT);
        if (endpoint.hostname !== '127.0.0.1' || endpoint.protocol !== 'http:' || endpoint.pathname !== '/tool') throw new Error('invalid_relay');
        const response = await fetch(endpoint, { method: 'POST', redirect: 'error',
          headers: { 'Content-Type': 'application/json', Authorization: 'Bearer ' + process.env.MIKU_TOOL_NONCE },
          body: JSON.stringify(packet), signal: AbortSignal.any([exec.signal, AbortSignal.timeout(200000)]) });
        if (!response.ok) throw new Error('miku_tool_unavailable');
        const result = await response.json();
        return JSON.stringify(result);
      }
    }));
  }
}
