/**
 * MPE-290: the auth boundary against the session store — getSession
 * resolves the opaque handle, dead handles get swept with all legacy
 * chunks, endSession revokes server-side before clearing locally.
 * next/headers + the store client are mocked; no real fetch happens.
 */

// jose is ESM-only — mock it (finishLogin's decodeJwt isn't under test).
jest.mock("jose", () => ({ decodeJwt: jest.fn() }));
jest.mock("next/headers", () => ({ cookies: jest.fn() }));
jest.mock("@/lib/session-store", () => ({
  createSession: jest.fn(),
  resolveSession: jest.fn(),
  revokeSession: jest.fn(),
}));
jest.mock("@/lib/session-env", () => ({
  assertSessionEnv: jest.fn(),
  isStrictSessionEnv: () => false,
}));

import { cookies } from "next/headers";
import { getSession, endSession } from "@/lib/auth";
import { resolveSession, revokeSession } from "@/lib/session-store";
import { HANDLE_COOKIE } from "@/lib/session-cookies";

function fakeStore(initial: Record<string, string>) {
  const jar = new Map(Object.entries(initial));
  return {
    jar,
    store: {
      get: (n: string) =>
        jar.has(n) ? { value: jar.get(n) as string } : undefined,
      getAll: () =>
        [...jar].map(([name, value]) => ({ name, value })),
      set: (n: string, v: string) => void jar.set(n, v),
      delete: (n: string) => void jar.delete(n),
    },
  };
}

beforeEach(() => {
  jest.clearAllMocks();
});

describe("getSession", () => {
  it("returns the resolved session", async () => {
    const { jar, store } = fakeStore({ [HANDLE_COOKIE]: "h1" });
    (cookies as jest.Mock).mockResolvedValue(store);
    (resolveSession as jest.Mock).mockResolvedValue({
      accessToken: "at-1",
      subject: "u1",
      name: "Officer",
    });
    const s = await getSession();
    expect(s).toEqual({ at: "at-1", subject: "u1", name: "Officer" });
    expect(resolveSession).toHaveBeenCalledWith("h1");
    expect(jar.get(HANDLE_COOKIE)).toBe("h1");
  });

  it("sweeps legacy chunks on a live session", async () => {
    const { jar, store } = fakeStore({
      [HANDLE_COOKIE]: "h",
      "lola_session.0": "old",
      lola_session: "old",
    });
    (cookies as jest.Mock).mockResolvedValue(store);
    (resolveSession as jest.Mock).mockResolvedValue({
      accessToken: "at",
      subject: "u",
    });
    await getSession();
    expect([...jar.keys()]).toEqual([HANDLE_COOKIE]);
  });

  it("deletes the handle and legacy chunks when resolve denies", async () => {
    const { jar, store } = fakeStore({
      [HANDLE_COOKIE]: "dead",
      "lola_session.3": "stale",
    });
    (cookies as jest.Mock).mockResolvedValue(store);
    (resolveSession as jest.Mock).mockResolvedValue(null);
    expect(await getSession()).toBeNull();
    expect(jar.size).toBe(0);
  });

  it("clears legacy chunks even with no handle — they never authenticate", async () => {
    const { jar, store } = fakeStore({
      "lola_session.0": "x",
      lola_session: "x",
    });
    (cookies as jest.Mock).mockResolvedValue(store);
    expect(await getSession()).toBeNull();
    expect(resolveSession).not.toHaveBeenCalled();
    expect(jar.size).toBe(0);
  });
});

describe("endSession", () => {
  it("revokes the server-side session before clearing cookies", async () => {
    const { jar, store } = fakeStore({ [HANDLE_COOKIE]: "h9" });
    (cookies as jest.Mock).mockResolvedValue(store);
    await endSession();
    expect(revokeSession).toHaveBeenCalledWith("h9");
    expect(jar.size).toBe(0);
  });

  it("tolerates a missing handle", async () => {
    const { jar, store } = fakeStore({ "lola_session.0": "x" });
    (cookies as jest.Mock).mockResolvedValue(store);
    await endSession();
    expect(revokeSession).not.toHaveBeenCalled();
    expect(jar.size).toBe(0);
  });
});
