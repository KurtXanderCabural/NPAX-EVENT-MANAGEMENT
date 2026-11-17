# Fix deployed login calling localhost

The web app is hosted at `https://nxpert-events.n-pax.com`. Its API is a separate
IIS site bound to `https://nxpert-events.n-pax.com:9000`.
The browser must post login requests to
`https://nxpert-events.n-pax.com:9000/api/auth/login`.
`http://localhost:5088` addresses the visitor's computer and is only for local development.

## Public checks on November 10, 2026

- API `https://nxpert-events.n-pax.com:9000/`: HTTP 200 with the expected health response.
- Login CORS preflight from `https://nxpert-events.n-pax.com`: HTTP 204 with that origin allowed.
- Frontend HTTPS on port 443: certificate name mismatch. The server presented
  `CN=www.palgolf.com.ph` with SANs `www.palgolf.com.ph` and `palgolf.com.ph`, which
  do not cover `nxpert-events.n-pax.com`.

For the **frontend site's** HTTPS binding, use port **443**, host name
`nxpert-events.n-pax.com`, and a certificate covering that name (or `*.n-pax.com`).
Enable SNI when multiple HTTPS sites share this IP and port. Confirm the request
reaches `EventPassAPP_NPAX` rather than another site's default binding. The API's
port-9000 binding in the screenshot is a separate binding and already responded correctly.

## Build and upload

From `frontend/web`, run `npm run build`. This explicitly selects production and
checks the emitted JavaScript for the public API URL and accidental local API URLs.
`npm run build:development` is for local use only.

Copy the **contents** of `frontend/web/dist/web/browser` into the physical folder
of the **frontend** IIS site (`EventPassAPP_NPAX` in the supplied IIS screenshot).
The prepared ZIP contains these files at its root; back up the current frontend
folder before replacing its files. Do not place these files in the API site folder.
Include `index.html`, all hashed JavaScript/CSS chunks, `web.config`, `ngsw.json`,
and the remaining generated assets together.

The API binding shown in IIS already uses HTTPS, host `nxpert-events.n-pax.com`,
port `9000`. Keep this binding consistent with `src/environments/environment.ts`.
Build again after changing the API URL.

## Verify the server and browser

1. On the IIS server, open `https://nxpert-events.n-pax.com:9000/` and confirm the
   API health response `{"service":"EventRegistration.Api","status":"ok"}`.
2. Open the same URL from the affected user's computer. Its certificate must be
   trusted and port 9000 must be allowed by the server firewall and any AWS security
   group, load balancer, or proxy in front of IIS. A binding does not open those paths.
3. If step 1 fails, inspect the API site's app pool, Windows Application event log,
   Hosting Bundle installation, and API database connection. If step 1 passes but
   step 2 fails, inspect the public network/firewall/TLS path.
4. After uploading, test the frontend in a new InPrivate/Incognito window. If an
   existing window still requests localhost, open DevTools > Application > Service
   Workers and unregister this site's worker. In Application > Cache Storage, remove
   this site's `ngsw` caches; in Network enable Disable cache, then press Ctrl+Shift+R.
   Close other tabs of the same site before reopening. Keep Local Storage and IndexedDB
   intact so saved sessions and any unsynchronized offline attendance records remain.
   Purge a reverse proxy/CDN cache if one is used.
5. In DevTools > Network, sign in and confirm the request URL ends with
   `:9000/api/auth/login`. A successful login returns HTTP 200. HTTP 401 means the API
   was reached but rejected the credentials. Connection refused, TLS errors, or 504
   require checking the API/network path; they are not password errors.

The install-banner message in the screenshot does not explain the failed login.

## All modules showing network errors after login

Login can appear to work when a saved session is present while calendar, dashboard,
and notifications still fail. HTTP services and the SignalR notification hub all use
`API_BASE` from `src/app/core/config.ts`. Seeing
`http://localhost:5088/hubs/notifications/negotiate` or `ws://localhost:5088` in the
deployed browser proves that session is still using the local API setting.

The corrected build resolves a loopback API setting to
`https://nxpert-events.n-pax.com:9000` when running on `nxpert-events.n-pax.com`,
including when a development bundle was mistakenly uploaded. Local development on
localhost continues to use port 5088. Production builds also reject local API URLs.
IIS revalidates `index.html` and service-worker metadata, and the app offers a Reload
button once a new service-worker version is ready. Deploy the complete rebuilt
frontend, then perform the one-time cache cleanup above to replace any existing old
version. New code cannot change the URL settings of JavaScript already running in a tab.

Afterward, check both a module request (for example `/api/calendar`) and notifications:
HTTP requests must use `https://nxpert-events.n-pax.com:9000`, while SignalR uses that
same base and `wss://nxpert-events.n-pax.com:9000` when WebSockets are selected.
