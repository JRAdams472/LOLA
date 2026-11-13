/// JSON1 application-document helpers — paths use the catalog syntax:
/// `subject_property.street`, `party[0].address[0].street`. Segments
/// split on dots; bracketed indexes traverse arrays.

export type Doc = Record<string, unknown>;

function segments(path: string): (string | number)[] {
  return path.split(".").map((seg) => {
    const m = /^(.+)\[(\d+)\]$/.exec(seg);
    return m ? [m[1], Number(m[2])] : [seg];
  }).flat();
}

export function getPath(doc: Doc | null | undefined, path: string): string | undefined {
  let node: unknown = doc;
  for (const seg of segments(path)) {
    if (node === null || typeof node !== "object") return undefined;
    node = (node as Record<string | number, unknown>)[seg];
  }
  if (node === undefined || node === null) return undefined;
  return typeof node === "string" ? node : String(node);
}

export function setPath(doc: Doc, path: string, value: string): Doc {
  const segs = segments(path);
  let node: Record<string | number, unknown> = doc;
  for (let i = 0; i < segs.length - 1; i++) {
    const seg = segs[i];
    const next = node[seg];
    if (next === null || typeof next !== "object") {
      node[seg] = typeof segs[i + 1] === "number" ? [] : {};
    }
    node = node[seg] as Record<string | number, unknown>;
  }
  node[segs[segs.length - 1]] = value;
  return doc;
}

/** A masked catalog leaf renders "•••"+last-4 — the only PII marker the
 *  masked doc carries; anything else starting with ••• is equally masked. */
export const isMasked = (v: string | undefined) => v?.startsWith("•••") ?? false;
