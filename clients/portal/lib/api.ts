// Same-origin BFF — the access token lives in the sealed session
// cookie and /bff attaches it server-side. A 401 means the session
// expired (idle or absolute): bounce through the login flow.
const BFF = "/bff";

function bounceOn401(res: Response) {
  if (res.status === 401 && typeof window !== "undefined") {
    // eslint-disable-next-line @next/next/no-location-assign-relative-destination -- plain lib code (no useRouter); /auth/login is a route handler, needs a document navigation
    window.location.assign(`/auth/login?next=${encodeURIComponent(window.location.pathname)}`);
  }
}

export async function apiPost<T>(
  path: string,
  body?: unknown,
): Promise<{ status: number; data: T }> {
  const res = await fetch(`${BFF}${path}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  bounceOn401(res);
  const data = (await res.json().catch(() => ({}))) as T;
  return { status: res.status, data };
}

/** GraphQL reads (ADR-0011) — same BFF custody, POST body only, so a
 *  sensitive term never lands in a URL, log line, or browser history. */
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
    throw new Error(
      payload.errors?.[0]?.message ?? `GraphQL failed: ${res.status}`,
    );
  }
  return payload.data as T;
}
