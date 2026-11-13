/** `…/applications/{appId}/uploads` is the only route allowed past the
 *  default BFF body cap (vault documents ≤25 MiB stream through). */
export function isVaultUpload(path: string[]): boolean {
  return (
    path.length === 6 &&
    path[0] === "api" &&
    path[1] === "tenants" &&
    path[3] === "applications" &&
    path[5] === "uploads"
  );
}
