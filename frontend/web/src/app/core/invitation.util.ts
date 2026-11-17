export const ALL_USERS = '@n-pax';

export function savedRecipientKey(userId: number | undefined): string | null {
  return userId ? `evt_saved_recipients_${userId}` : null;
}

export function loadSavedRecipients(userId: number | undefined): string[] {
  const key = savedRecipientKey(userId);
  if (!key) return [];
  try {
    const entries: unknown = JSON.parse(localStorage.getItem(key) ?? '[]');
    if (!Array.isArray(entries)) return [];
    return entries.map((entry) => typeof entry === 'string' ? recipientAddress(entry) : null)
      .filter((address): address is string => !!address && address !== ALL_USERS);
  } catch {
    return [];
  }
}

export function invitationErrorMessage(error: { status?: number; error?: unknown }): string {
  const body = error.error;
  const draft = ' Your draft has been kept.';
  if (body && typeof body === 'object') {
    const problem = body as { message?: unknown; errors?: Record<string, unknown> };
    if (typeof problem.message === 'string' && problem.message.trim()) return problem.message + draft;
    const details = Object.values(problem.errors ?? {}).flat().filter((v): v is string => typeof v === 'string');
    if (details.length) return details.join(' ') + draft;
  }
  switch (error.status) {
    case 404:
    case 405:
      return 'The invitation composer endpoint is unavailable on the API server. Deploy the updated backend, then reload this page.' + draft;
    case 0:
      return 'Cannot reach the email API. Check the connection and server availability.' + draft;
    case 401:
      return 'Your session has expired. Sign in again before sending invitations.' + draft;
    case 403:
      return 'Only an administrator can send event invitations.' + draft;
    case 413:
      return 'The server rejected the message size. Use smaller images or update the server upload limit.' + draft;
    case 400:
    case 422:
      return 'The server rejected the invitation. Check the recipients, subject and image limits.' + draft;
    default:
      return `The API could not process the invitation${error.status ? ` (HTTP ${error.status})` : ''}. Check the server logs before retrying.` + draft;
  }
}

export function recipientAddress(value: string): string | null {
  const address = value.trim().toLowerCase();
  if (address === ALL_USERS) return address;
  return address.length <= 256 && /^[^\s<>@,;]+@[^\s<>@,;]+\.[^\s<>@,;]+$/.test(address) ? address : null;
}

export function expandRecipients(to: string[], cc: string[], directory: string[], excludedTo: Iterable<string> = []): { to: string[]; cc: string[] } {
  const users = directory.map(recipientAddress).filter((e): e is string => !!e && e !== ALL_USERS);
  const expand = (entries: string[]) => [...new Set(entries.flatMap(entry => {
    const address = recipientAddress(entry);
    return address === ALL_USERS ? users : address ? [address] : [];
  }))];
  const copies = expand(cc);
  const omitted = new Set([...copies, ...Array.from(excludedTo, (address) => address.trim().toLowerCase())]);
  return { to: expand(to).filter(address => !omitted.has(address)), cc: copies };
}
/** Formats Philippine wall-clock values without applying the browser's time zone. */
export function formatScheduleLabel(value: string, longDay = false): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/.exec(value);
  if (!match) return '';
  const [year, month, day, hour, minute] = match.slice(1).map(Number);
  const utc = new Date(Date.UTC(year, month - 1, day, hour, minute));
  const date = utc.toLocaleDateString('en-US', { timeZone: 'UTC', weekday: longDay ? 'long' : 'short',
    month: longDay ? 'long' : 'short', day: 'numeric', year: 'numeric' });
  const time = utc.toLocaleTimeString('en-US', { timeZone: 'UTC', hour: 'numeric', minute: '2-digit' });
  return `${date} · ${time}`;
}
