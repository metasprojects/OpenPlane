// Talks to OpenPlane's local server. Every failure becomes an ApiError whose message is written for the player.

export class ApiError extends Error {
  constructor(message, status) { super(message); this.status = status; }
}

async function request(method, url, body, { raw = false, headers = {} } = {}) {
  const init = { method, headers: { 'X-OpenPlane': '1', ...headers } };
  if (body !== undefined) {
    if (raw) init.body = body;
    else { init.body = JSON.stringify(body); init.headers['Content-Type'] = 'application/json'; }
  }
  let res;
  try { res = await fetch(url, init); }
  catch { throw new ApiError("OpenPlane's local server isn't answering. Close OpenPlane and open it again.", 0); }
  const text = await res.text();
  let data = null;
  if (text) {
    try { data = JSON.parse(text); }
    catch { if (!res.ok) throw new ApiError(`The server answered ${res.status} ${res.statusText}.`, res.status); }
  }
  if (!res.ok) throw new ApiError(data?.error || `The server answered ${res.status} ${res.statusText}.`, res.status);
  return data;
}

export const api = {
  get: url => request('GET', url),
  post: (url, body = {}) => request('POST', url, body),
  put: (url, body = {}) => request('PUT', url, body),
  del: url => request('DELETE', url),
  upload: (url, blob) => request('POST', url, blob, { raw: true, headers: { 'Content-Type': 'application/octet-stream' } }),
};

/** Status is asked for often; share one request at a time. */
let statusPromise = null;
export function getStatus(force = false) {
  if (!statusPromise || force) {
    statusPromise = api.get('/api/status').finally(() => setTimeout(() => { statusPromise = null; }, 1500));
  }
  return statusPromise;
}
