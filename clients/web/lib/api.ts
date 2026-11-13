// Same-origin BFF — the access token lives in an httpOnly cookie and the
// /bff route attaches it server-side. A 401 means the session is gone:
// bounce through the login flow rather than surfacing a raw error.
const BFF = "/bff";

function bounceOn401(res: Response) {
  if (res.status === 401 && typeof window !== "undefined") {
    // eslint-disable-next-line @next/next/no-location-assign-relative-destination -- plain lib code (no useRouter); /auth/login is a route handler, needs a document navigation
    window.location.assign(`/auth/login?next=${encodeURIComponent(window.location.pathname)}`);
  }
}

export async function apiGet<T>(path: string): Promise<T> {
  const res = await fetch(`${BFF}${path}`);
  bounceOn401(res);
  if (!res.ok) {
    throw new Error(`API ${path} failed: ${res.status}`);
  }
  return res.json() as Promise<T>;
}

async function apiWrite<T>(
  method: string,
  path: string,
  body?: unknown,
): Promise<{ status: number; data: T }> {
  const res = await fetch(`${BFF}${path}`, {
    method,
    headers: { "content-type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  bounceOn401(res);
  const data = (await res.json().catch(() => ({}))) as T;
  return { status: res.status, data };
}

export const apiPost = <T>(path: string, body?: unknown) =>
  apiWrite<T>("POST", path, body);
export const apiPut = <T>(path: string, body: unknown) =>
  apiWrite<T>("PUT", path, body);
export const apiPatch = <T>(path: string, body: unknown) =>
  apiWrite<T>("PATCH", path, body);
export const apiDelete = (path: string) => apiWrite("DELETE", path);

/** GraphQL reads (ADR-0011) — same BFF custody, POST body only, so a
 *  search term (e.g. an SSN) never lands in a URL, log line, or
 *  browser history. */
export async function gql<T>(
  query: string,
  variables?: Record<string, unknown>,
): Promise<T> {
  const res = await fetch(`${BFF}/graphql`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ query, variables }),
  });
  bounceOn401(res);
  const payload = (await res.json()) as {
    data?: T;
    errors?: { message: string }[];
  };
  if (!res.ok || payload.errors?.length) {
    throw new Error(payload.errors?.[0]?.message ?? `GraphQL failed: ${res.status}`);
  }
  return payload.data as T;
}

/** Error message from an apiWrite-backed mutation result, or null when clean. */
export function mutationError(m: {
  data?: { status: number; data: unknown };
  isError: boolean;
  error: unknown;
}): string | null {
  if (m.data && m.data.status >= 400) {
    const data = m.data.data as { title?: string; detail?: string; error?: string };
    return data.detail ?? data.title ?? data.error ?? `HTTP ${m.data.status}`;
  }
  return m.isError ? `${m.error}` : null;
}
