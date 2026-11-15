import type { ApiClient } from '@/lib/api';
import type { Platform } from '@/platform';
import type { Session } from '@/types/api';

/** Providers that take a BYOK key (mirrors the backend's ProviderNames.KeyedProviders). */
const KEYED_PROVIDERS = new Set(['deepgram', 'anthropic', 'openai', 'google']);

/**
 * Takes a session live and starts audio capture. Shared by the session page and the meeting prompt
 * (#16). Between going live and capturing, the keys of the chosen cloud providers are handed to the
 * backend (#30), which keeps them in memory for this session only.
 */
export async function startLiveSession(api: ApiClient, platform: Platform, sessionId: string, onStarted?: (session: Session) => void): Promise<Session> {
  const started = await api.startSession(sessionId);
  onStarted?.(started);
  await handOffKeys(api, platform, sessionId);
  await platform.startCapture(sessionId);
  return started;
}

/** A required key hand-off must succeed before capture starts. */
async function handOffKeys(api: ApiClient, platform: Platform, sessionId: string): Promise<void> {
  const prefs = await api.getPreferences();
  const providers = [
    ...new Set([prefs.llmProvider ?? prefs.defaultLlmProvider, prefs.speechToTextProvider ?? prefs.defaultSpeechToTextProvider]),
  ].filter((name) => KEYED_PROVIDERS.has(name));

  for (const provider of providers) {
    const key = await platform.getApiKey(provider);
    if (key) {
      await api.setSessionKey(sessionId, provider, key);
    }
  }
}
