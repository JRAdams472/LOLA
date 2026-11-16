import {
  absoluteExpired,
  idleExpired,
  needsTouch,
  sessionMaxAgeS,
  IDLE_MS,
  ABSOLUTE_MS,
  TOUCH_MIN_MS,
} from "@/lib/session-policy";

const T = 1_800_000_000_000; // arbitrary epoch-ms "now"

describe("idleExpired", () => {
  it("expires a session idle longer than the window", () => {
    expect(idleExpired(T - IDLE_MS - 1, T)).toBe(true);
    expect(idleExpired(T - IDLE_MS, T)).toBe(false);
  });

  it("keeps an active session alive", () => {
    expect(idleExpired(T - 60_000, T)).toBe(false);
    expect(idleExpired(T, T)).toBe(false);
  });

  it("never honors a session with no lastActive", () => {
    expect(idleExpired(undefined, T)).toBe(true);
  });
});

describe("absoluteExpired", () => {
  it("expires at the absolute bound", () => {
    expect(absoluteExpired(T, T)).toBe(true);
    expect(absoluteExpired(T - 1, T)).toBe(true); // bound already passed
    expect(absoluteExpired(T + 1, T)).toBe(false);
  });

  it("never honors a session with no absolute bound", () => {
    expect(absoluteExpired(undefined, T)).toBe(true);
  });
});

describe("needsTouch", () => {
  it("slides the window only past the touch threshold", () => {
    expect(needsTouch(T - TOUCH_MIN_MS + 1, T)).toBe(false);
    expect(needsTouch(T - TOUCH_MIN_MS, T)).toBe(true);
    expect(needsTouch(undefined, T)).toBe(true);
  });
});

describe("sessionMaxAgeS", () => {
  it("bounds the cookie by the earliest of cap, rt, and absolute expiry", () => {
    const capS = Math.ceil(ABSOLUTE_MS / 1000);
    // cap alone
    expect(sessionMaxAgeS(undefined, undefined, T, 3600)).toBe(3600);
    // rt dies first
    expect(sessionMaxAgeS(undefined, T + 120_000, T, capS)).toBe(120);
    // absolute bound dies first
    expect(sessionMaxAgeS(T + 60_000, T + 999_000_000, T, capS)).toBe(60);
  });

  it("never returns less than one second", () => {
    expect(sessionMaxAgeS(T - 5_000, undefined, T, 3600)).toBe(1);
  });
});
