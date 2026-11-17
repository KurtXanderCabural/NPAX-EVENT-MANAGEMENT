const DEPLOYED_HOST = 'nxpert-events.n-pax.com';
const DEPLOYED_API = 'https://nxpert-events.n-pax.com:9000';

/** All HTTP services and SignalR use this same resolved base URL. */
export function resolveApiBase(configuredBase: string, pageHostname: string): string {
  const configured = new URL(configuredBase);
  const loopback = configured.hostname === 'localhost' || configured.hostname.endsWith('.localhost') ||
    /^127\./.test(configured.hostname) || configured.hostname === '[::1]';
  // A development bundle uploaded to the known public site must never send
  // credentials or API requests to a visitor's local machine.
  if (pageHostname.toLowerCase() === DEPLOYED_HOST && loopback) return DEPLOYED_API;
  return configuredBase.replace(/\/+$/, '');
}
