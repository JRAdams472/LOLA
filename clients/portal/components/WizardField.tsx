import { useState } from "react";

import { isMasked } from "@/lib/doc";
import type { FieldSpec } from "@/lib/field-map";

const enumLabel = (v: string) =>
  v.split("_").map((w) => w[0].toUpperCase() + w.slice(1)).join(" ");

const inputCls =
  "mt-1 w-full rounded-md border border-border bg-canvas px-3 py-2 text-sm text-text placeholder:text-faint focus:border-accent";

/**
 * One catalog field for the borrower wizard. Masked-confirm: a saved
 * PII value renders "•••"+last-4 as text; choosing "Change" swaps in an
 * empty input — the mask itself is never editable and never sent back.
 */
export default function WizardField({
  spec,
  value,
  error,
  onChange,
}: {
  spec: FieldSpec;
  value: string | undefined;
  error?: string;
  onChange: (path: string, value: string) => void;
}) {
  const masked = isMasked(value);
  const [replacing, setReplacing] = useState(false);

  if (masked && !replacing) {
    return (
      <div>
        <span className="block text-sm text-muted">
          {spec.label}
          {spec.required ? " *" : ""}
        </span>
        <div className="mt-1 flex items-center justify-between gap-2 rounded-md border border-border bg-surface-2 px-3 py-2">
          <span className="text-sm tracking-wide text-text">{value}</span>
          <button
            type="button"
            onClick={() => setReplacing(true)}
            className="text-xs text-accent underline-offset-4 hover:underline"
          >
            Change
          </button>
        </div>
        {error && <p className="mt-1 text-xs text-danger">{error}</p>}
      </div>
    );
  }

  const editValue = masked ? "" : (value ?? "");
  const label = (
    <>
      {spec.label}
      {spec.required ? " *" : ""}
    </>
  );

  return (
    <div>
      <label htmlFor={spec.path} className="block text-sm text-muted">
        {label}
      </label>
      {spec.enum || spec.type === "bool" ? (
        <select
          id={spec.path}
          value={editValue}
          onChange={(e) => onChange(spec.path, e.target.value)}
          className={inputCls}
        >
          <option value="">—</option>
          {(spec.enum ?? ["true", "false"]).map((v) => (
            <option key={v} value={v}>
              {v === "true" ? "Yes" : v === "false" ? "No" : enumLabel(v)}
            </option>
          ))}
        </select>
      ) : (
        <input
          id={spec.path}
          type={spec.type === "date" ? "date" : "text"}
          inputMode={
            spec.type === "decimal" || spec.type === "int"
              ? "decimal"
              : undefined
          }
          placeholder={masked ? "Enter new value" : undefined}
          value={editValue}
          onChange={(e) => onChange(spec.path, e.target.value)}
          className={inputCls}
        />
      )}
      {error && (
        <p className="mt-1 text-xs text-danger" role="alert">
          {error}
        </p>
      )}
    </div>
  );
}
