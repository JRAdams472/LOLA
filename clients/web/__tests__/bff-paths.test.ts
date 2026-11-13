import { isVaultUpload } from "@/lib/bff-paths";

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
