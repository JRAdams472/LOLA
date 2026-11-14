import { hasUnsafeSegment, isVaultUpload } from "@/lib/bff-paths";

describe("isVaultUpload", () => {
  it("allows the uploads collection route", () => {
    expect(
      isVaultUpload(["api", "tenants", "t1", "applications", "a1", "uploads"]),
    ).toBe(true);
  });

  it("rejects lookalike and nested routes", () => {
    expect(
      isVaultUpload(["api", "tenants", "t1", "applications", "a1", "uploads", "d1"]),
    ).toBe(false);
    expect(isVaultUpload(["api", "tenants", "t1", "uploads"])).toBe(false);
    expect(
      isVaultUpload(["api", "tenants", "t1", "applications", "a1", "documents"]),
    ).toBe(false);
    expect(isVaultUpload(["graphql"])).toBe(false);
    expect(isVaultUpload([])).toBe(false);
  });
});

describe("hasUnsafeSegment", () => {
  it("rejects dot segments that would escape the /api prefix", () => {
    expect(hasUnsafeSegment(["api", ".."])).toBe(true);
    expect(hasUnsafeSegment(["api", ".", "tenants"])).toBe(true);
    expect(hasUnsafeSegment(["api", "..", "..", "admin"])).toBe(true);
  });

  it("rejects backslashes — WHATWG URL parsing folds \\ to /", () => {
    expect(hasUnsafeSegment(["api", "..\\..", "admin"])).toBe(true);
    expect(hasUnsafeSegment(["api", "tenants", "x\\..\\.."])).toBe(true);
  });

  it("rejects raw slashes — a decoded %2F smuggles segments", () => {
    expect(hasUnsafeSegment(["api", "x/../.."])).toBe(true);
  });

  it("allows ordinary segments, dotted ids, and emails", () => {
    expect(hasUnsafeSegment(["api", "tenants", "demo"])).toBe(false);
    expect(hasUnsafeSegment(["api", "tenants", "t", "applications",
      "app-1a2b", "documents", "doc-9f"])).toBe(false);
    expect(hasUnsafeSegment(["api", "admin", "users", "a.b@c.d"])).toBe(false);
    expect(hasUnsafeSegment(["graphql"])).toBe(false);
  });
});
