import { safeNextPath } from "../lib/nav";

describe("safeNextPath", () => {
  it("keeps same-origin paths", () => {
    expect(safeNextPath("/")).toBe("/");
    expect(safeNextPath("/loans/app-1")).toBe("/loans/app-1");
    expect(safeNextPath("/search?f=1")).toBe("/search?f=1");
  });

  it("rejects missing and non-path input", () => {
    expect(safeNextPath(null)).toBe("/");
    expect(safeNextPath(undefined)).toBe("/");
    expect(safeNextPath("")).toBe("/");
    expect(safeNextPath("https://evil.com")).toBe("/");
    expect(safeNextPath("loans/1")).toBe("/");
  });

  it("rejects //host", () => {
    expect(safeNextPath("//evil.com")).toBe("/");
  });

  it("rejects backslash traversal — WHATWG normalizes \\ to /", () => {
    // new URL("/\\evil.com", origin) resolves to http://evil.com/ — an
    // off-site open redirect if only "//" were checked.
    expect(safeNextPath("/\\evil.com")).toBe("/");
    expect(safeNextPath("/\\/evil.com")).toBe("/");
    expect(safeNextPath("/%5cevil.com".replace("%5c", "\\"))).toBe("/");
  });
});
