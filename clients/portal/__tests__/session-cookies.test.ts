import {
  HANDLE_COOKIE,
  LEGACY_COOKIE,
  clearLegacyCookies,
  isLegacySessionCookie,
  type CookieStore,
} from "@/lib/session-cookies";

/** MPE-290: cookie mechanics — one opaque `__Host-` handle; every
 *  pre-cutover chunk is legacy and unconditionally swept. */

describe("isLegacySessionCookie", () => {
  it.each([
    ["lola_portal", true],
    ["lola_portal.0", true],
    ["lola_portal.12", true],
    ["__Host-lola_portal", false],
    ["lola_portalX", false],
    ["lola_session", false],
    ["lola_portal_pkce", false],
  ])("%s → %s", (name, expected) => {
    expect(isLegacySessionCookie(name)).toBe(expected);
  });

  it("constants keep the naming contract", () => {
    expect(HANDLE_COOKIE).toBe("__Host-lola_portal");
    expect(LEGACY_COOKIE).toBe("lola_portal");
  });
});

describe("clearLegacyCookies", () => {
  function jar(entries: Record<string, string>): CookieStore & {
    deleted: string[];
  } {
    const deleted: string[] = [];
    return {
      deleted,
      get: (n) => (n in entries ? { value: entries[n] } : undefined),
      getAll: () =>
        Object.entries(entries).map(([name, value]) => ({ name, value })),
      delete: (n) => deleted.push(n),
    };
  }

  it("deletes every legacy chunk, keeps everything else", () => {
    const store = jar({
      [HANDLE_COOKIE]: "handle",
      "lola_portal.0": "a",
      "lola_portal.1": "b",
      lola_portal: "c",
      lola_portal_pkce: "pkce",
      other: "x",
    });
    clearLegacyCookies(store);
    expect(store.deleted.sort()).toEqual([
      "lola_portal",
      "lola_portal.0",
      "lola_portal.1",
    ]);
  });

  it("is a no-op when no legacy cookies exist", () => {
    const store = jar({ [HANDLE_COOKIE]: "h" });
    clearLegacyCookies(store);
    expect(store.deleted).toEqual([]);
  });
});
