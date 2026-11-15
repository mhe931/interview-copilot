import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

describe('Content Security Policy', () => {
  it('allows the session events WebSocket on the local backend', async () => {
    const html = await readFile(path.join(process.cwd(), 'index.html'), 'utf8');

    expect(html).toMatch(/connect-src[^"]*ws:\/\/127\.0\.0\.1:5480/);
  });
});
