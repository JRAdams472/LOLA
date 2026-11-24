"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import { apiPatch, apiPost, gql } from "@/lib/api";
import { getPath, isMasked, type Doc } from "@/lib/doc";
import { FIELD_MAP, type SectionId } from "@/lib/field-map";
import type { PortalLoan } from "@/lib/loans";

/** RFC-9457 422 payload — `errors` rides the problem-details
 *  extensions bag; each entry keys an exact catalog path. */
export interface FieldError {
  path: string;
  code: string;
  message: string;
}

const LOAN_FIELDS = `
  id statusLabel statusStage
  statusStages { key label done current }
  docJson
  uploads { vaultDocId classification statusLabel statusKey
            filename sizeBytes uploadedUtc }
  documents { docId packageName kindLabel ready filename completedUtc }`;

export type WizardState =
  | { kind: "loading" }
  | { kind: "error"; error: string }
  | { kind: "none" } // no application yet — offer to start one
  | { kind: "submitted"; loan: PortalLoan } // tracker handles this
  | {
      kind: "ready";
      appId: string;
      doc: Doc;
      edits: Record<string, string>;
      dirty: boolean;
      saving: boolean;
      fieldErrors: Record<string, string>;
      submitErrors: FieldError[];
    };

type Action =
  | { t: "loaded"; appId: string; doc: Doc }
  | { t: "none" }
  | { t: "submitted"; loan: PortalLoan }
  | { t: "error"; error: string }
  | { t: "edit"; path: string; value: string }
  | { t: "saving"; on: boolean }
  | { t: "saved" }
  | { t: "submit-failed"; errors: FieldError[] };

function reduce(state: WizardState, a: Action): WizardState {
  switch (a.t) {
    case "loaded":
      return {
        kind: "ready", appId: a.appId, doc: a.doc, edits: {},
        dirty: false, saving: false, fieldErrors: {}, submitErrors: [],
      };
    case "edit":
      if (state.kind !== "ready") return state;
      return {
        ...state,
        edits: { ...state.edits, [a.path]: a.value },
        dirty: true,
        fieldErrors: omit(state.fieldErrors, a.path),
      };
    case "saving":
      return state.kind === "ready" ? { ...state, saving: a.on } : state;
    case "saved":
      if (state.kind !== "ready") return state;
      return {
        ...state,
        doc: mergeEdits(state.doc, state.edits),
        edits: {}, dirty: false, saving: false,
        submitErrors: [],
      };
    case "submit-failed":
      if (state.kind !== "ready") return state;
      return {
        ...state,
        saving: false,
        submitErrors: a.errors,
        fieldErrors: Object.fromEntries(
          a.errors.map((e) => [e.path, e.message]),
        ),
      };
    case "none": return { kind: "none" };
    case "submitted": return { kind: "submitted", loan: a.loan };
    case "error": return { kind: "error", error: a.error };
  }
}

const omit = (o: Record<string, string>, k: string) => {
  const { [k]: _drop, ...rest } = o;
  return rest;
};

function mergeEdits(doc: Doc, edits: Record<string, string>): Doc {
  const out = JSON.parse(JSON.stringify(doc)) as Doc;
  for (const [path, value] of Object.entries(edits)) {
    const segs = path.split(".").flatMap((s) => {
      const m = /^(.+)\[(\d+)\]$/.exec(s);
      return m ? [m[1], Number(m[2])] : [s];
    });
    let node: Record<string | number, unknown> = out;
    for (let i = 0; i < segs.length - 1; i++) {
      const next = node[segs[i]];
      if (next === null || typeof next !== "object")
        node[segs[i]] = typeof segs[i + 1] === "number" ? [] : {};
      node = node[segs[i]] as Record<string | number, unknown>;
    }
    node[segs[segs.length - 1]] = value;
  }
  return out;
}

/** Display value: a typed edit wins; otherwise the (possibly masked)
 *  saved value. Masked values are display-only — see patchFor. */
export function displayValue(
  state: Extract<WizardState, { kind: "ready" }>,
  path: string,
): string | undefined {
  return state.edits[path] ?? getPath(state.doc, path);
}

/** The PATCH body — the masked-confirm contract: a masked saved value
 *  is never echoed back. An edit on a masked field must contain a real
 *  replacement (non-empty); an untouched masked field contributes
 *  nothing. Non-PII fields may clear to empty — that's a real edit. */
export function patchFor(
  state: Extract<WizardState, { kind: "ready" }>,
): Record<string, string> {
  const patch: Record<string, string> = {};
  for (const [path, value] of Object.entries(state.edits)) {
    const saved = getPath(state.doc, path);
    if (isMasked(saved) && value === "") continue; // opened but didn't retype
    if (value === saved) continue;                 // no-op edit
    patch[path] = value;
  }
  return patch;
}

/** Group 422 FieldError[] by wizard section for the review summary. */
export function errorsBySection(
  errors: FieldError[],
): { section: SectionId; errors: FieldError[] }[] {
  const groups = new Map<SectionId, FieldError[]>();
  for (const e of errors) {
    const section = FIELD_MAP[e.path]?.section ?? "loan";
    groups.set(section, [...(groups.get(section) ?? []), e]);
  }
  return [...groups.entries()].map(([section, errors]) => ({
    section,
    errors,
  }));
}

export function useWizard(tid: string | null | undefined) {
  const [state, setState] = useState<WizardState>({ kind: "loading" });
  // `liveRef` mirrors state synchronously inside dispatch — a click can
  // outrun the render that carries the last keystroke's edit, and a
  // stale closure reading `state` would silently drop it from the PATCH.
  const liveRef = useRef<WizardState>({ kind: "loading" });
  const dispatch = useCallback(
    (a: Action) => {
      liveRef.current = reduce(liveRef.current, a);
      setState((s) => reduce(s, a));
    },
    [],
  );

  const load = useCallback(async () => {
    if (!tid) return;
    try {
      const list = await gql<{ portalLoans: { id: string; statusStage: number }[] }>(
        `query L($t: String!) { portalLoans(tenantId: $t) { id statusStage } }`,
        { t: tid },
      );
      if (list.portalLoans.length === 0) return dispatch({ t: "none" });
      const draft = list.portalLoans.find((l) => l.statusStage === 0);
      if (!draft) {
        // All applications are past draft — nothing to edit here.
        const { portalLoan } = await gql<{ portalLoan: PortalLoan }>(
          `query P($t: String!, $id: String!) {
             portalLoan(tenantId: $t, appId: $id) { ${LOAN_FIELDS} } }`,
          { t: tid, id: list.portalLoans[0].id },
        );
        return dispatch({ t: "submitted", loan: portalLoan });
      }
      const { portalLoan } = await gql<{ portalLoan: PortalLoan }>(
        `query P($t: String!, $id: String!) {
           portalLoan(tenantId: $t, appId: $id) { ${LOAN_FIELDS} } }`,
        { t: tid, id: draft.id },
      );
      dispatch({
        t: "loaded",
        appId: draft.id,
        doc: JSON.parse(portalLoan.docJson) as Doc,
      });
    } catch (e) {
      dispatch({ t: "error", error: `${e}` });
    }
  }, [tid, dispatch]);

  useEffect(() => {
    load();
  }, [load]);

  const startApplication = useCallback(async () => {
    if (!tid) return;
    liveRef.current = { kind: "loading" };
    setState({ kind: "loading" });
    const res = await apiPost(`/api/tenants/${tid}/applications`, {});
    if (res.status >= 400) {
      dispatch({ t: "error", error: `HTTP ${res.status}` });
      return;
    }
    await load();
  }, [tid, load, dispatch]);

  const save = useCallback(async (): Promise<boolean> => {
    const s = liveRef.current;
    if (s.kind !== "ready" || !tid) return false;
    const patch = patchFor(s);
    if (Object.keys(patch).length === 0) return true; // nothing to save
    dispatch({ t: "saving", on: true });
    const res = await apiPatch(
      `/api/tenants/${tid}/applications/${s.appId}`,
      { patch },
    );
    if (res.status === 422) {
      const errs = (res.data as { errors?: FieldError[] }).errors ?? [];
      dispatch({ t: "submit-failed", errors: errs });
      return false;
    }
    if (res.status >= 400) {
      dispatch({ t: "saving", on: false });
      dispatch({ t: "error", error: `HTTP ${res.status}` });
      return false;
    }
    dispatch({ t: "saved" });
    return true;
  }, [tid, dispatch]);

  const submit = useCallback(async (): Promise<
    { result: "ok" } | { result: "errors"; errors: FieldError[] } | { result: "fail" }
  > => {
    const s = liveRef.current;
    if (s.kind !== "ready" || !tid) return { result: "fail" };
    if (!(await save())) return { result: "fail" }; // flush pending edits first
    dispatch({ t: "saving", on: true });
    const res = await apiPost(
      `/api/tenants/${tid}/applications/${s.appId}/submit`,
    );
    if (res.status === 422) {
      const errs = (res.data as { errors?: FieldError[] }).errors ?? [];
      dispatch({ t: "submit-failed", errors: errs });
      return { result: "errors", errors: errs };
    }
    if (res.status >= 400) {
      dispatch({ t: "saving", on: false });
      dispatch({ t: "error", error: `HTTP ${res.status}` });
      return { result: "fail" };
    }
    await load(); // re-read — status is now Under review
    return { result: "ok" };
  }, [tid, save, load, dispatch]);

  const peek = useCallback(() => liveRef.current, []);
  return useMemo(
    () => ({ state, peek, dispatch, load, startApplication, save, submit }),
    [state, peek, dispatch, load, startApplication, save, submit],
  );
}
