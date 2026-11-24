/**
 * M12 / MPE-290: portal session bounds — the env knobs must keep wiring
 * into the values handed to the API session store at create time.
 */
describe("portal session bounds", () => {
  const ORIG = process.env;
  beforeEach(() => {
    jest.resetModules();
    process.env = { ...ORIG };
  });
  afterAll(() => {
    process.env = ORIG;
  });

  it("defaults to 30m idle / 4h absolute", async () => {
    delete process.env.PORTAL_SESSION_IDLE_MIN;
    delete process.env.PORTAL_SESSION_MAX_H;
    const p = await import("@/lib/session-policy");
    expect(p.IDLE_MS).toBe(30 * 60_000);
    expect(p.ABSOLUTE_MS).toBe(4 * 3_600_000);
  });

  it("honors the env overrides", async () => {
    process.env.PORTAL_SESSION_IDLE_MIN = "0.05";
    process.env.PORTAL_SESSION_MAX_H = "1";
    const p = await import("@/lib/session-policy");
    expect(p.IDLE_MS).toBe(0.05 * 60_000);
    expect(p.ABSOLUTE_MS).toBe(3_600_000);
  });
});
