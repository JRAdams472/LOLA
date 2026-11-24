/**
 * MPE-290: the session-store client — cc-token caching, fail-closed
 * semantics (dead store ⇒ unauthenticated), and the credential gate.
 * fetch is mocked; env is set per test (module reads it lazily).
 */

const ORIG = process.env;
const STORE_ENV = {
  API_URL: "http://api.test:8200",
  BFF_CLIENT_ID: "lola-bff-portal",
  BFF_CLIENT_SECRET: "real-secret-material",
};

function tokenResponse() {
  return {
    ok: true,
    json: async () => ({ access_token: "cc-at", expires_in: 3600 }),
  } as Response;
}

beforeEach(() => {
  jest.resetModules();
  process.env = { ...ORIG, ...STORE_ENV };
});
afterAll(() => {
  process.env = ORIG;
});

async function importStore() {
  return await import("@/lib/session-store");
}

describe("resolveSession", () => {
  it("returns the usable access token on 200", async () => {
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce(tokenResponse())
      .mockResolvedValueOnce({
        ok: true,
        json: async () => ({
          accessToken: "user-at",
          atExpMs: 1,
          subject: "u1",
          name: "O",
        }),
      } as Response);
    global.fetch = fetchMock;
    const store = await importStore();
    const s = await store.resolveSession("handle-1");
    expect(s?.accessToken).toBe("user-at");
    // token mint then the resolve call, against API_URL.
    expect(fetchMock).toHaveBeenLastCalledWith(
      "http://api.test:8200/bff-sessions/resolve",
      expect.objectContaining({ method: "POST" }),
    );
  });

  it.each([401, 403, 500])("returns null on store %s", async (status) => {
    global.fetch = jest
      .fn()
      .mockResolvedValueOnce(tokenResponse())
      .mockResolvedValueOnce({ ok: false, status } as Response);
    const store = await importStore();
    expect(await store.resolveSession("h")).toBeNull();
  });

  it("fails closed on transport error", async () => {
    global.fetch = jest
      .fn()
      .mockResolvedValueOnce(tokenResponse())
      .mockRejectedValueOnce(new Error("ECONNREFUSED"));
    const store = await importStore();
    expect(await store.resolveSession("h")).toBeNull();
  });

  it("fails closed with no store credentials", async () => {
    delete process.env.BFF_CLIENT_SECRET;
    global.fetch = jest.fn();
    const store = await importStore();
    expect(await store.resolveSession("h")).toBeNull();
    expect(global.fetch).not.toHaveBeenCalled();
  });
});

describe("client-credentials token", () => {
  it("is cached across calls until expiry", async () => {
    global.fetch = jest
      .fn()
      .mockResolvedValueOnce(tokenResponse())
      .mockResolvedValue({
        ok: true,
        json: async () => ({ accessToken: "at", subject: "u" }),
      } as Response);
    const store = await importStore();
    await store.resolveSession("h1");
    await store.resolveSession("h2");
    // one token mint + two resolves
    expect(global.fetch).toHaveBeenCalledTimes(3);
    expect(global.fetch).toHaveBeenNthCalledWith(
      1,
      "http://api.test:8200/connect/token",
      expect.objectContaining({ method: "POST" }),
    );
  });
});

describe("createSession", () => {
  it("posts the grant and reports the store's verdict", async () => {
    global.fetch = jest
      .fn()
      .mockResolvedValueOnce(tokenResponse())
      .mockResolvedValueOnce({ ok: true } as Response);
    const store = await importStore();
    await expect(
      store.createSession({
        handle: "h",
        subject: "u",
        tenantId: "t",
        authVersion: 1,
        accessToken: "at",
        atExpMs: 1,
        absExpMs: 2,
      }),
    ).resolves.toBe(true);
    const body = JSON.parse(
      (global.fetch as jest.Mock).mock.calls[1][1].body as string,
    );
    expect(body.handle).toBe("h");
  });
});

describe("revokeSession", () => {
  it("is best-effort — transport failure does not throw", async () => {
    global.fetch = jest.fn().mockRejectedValue(new Error("down"));
    const store = await importStore();
    await expect(store.revokeSession("h")).resolves.toBeUndefined();
  });
});
