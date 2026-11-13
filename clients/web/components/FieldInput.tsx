import { useState } from "react";
import TextField from "@mui/material/TextField";
import MenuItem from "@mui/material/MenuItem";
import Box from "@mui/material/Box";
import Typography from "@mui/material/Typography";

import MaskedValue from "@/components/MaskedValue";
import type { FieldSpec } from "@/lib/field-map";
import { isMasked } from "@/lib/doc";

const enumLabel = (v: string) =>
  v.split("_").map((w) => w[0].toUpperCase() + w.slice(1)).join(" ");

/// One catalog field: label + editable input (enum→select, bool→yes/no,
/// date/decimal/int→text) or a MaskedValue when the server masked it.
/// Masked fields stay writable — the pencil swaps in an empty input so
/// the officer can replace a value they can't see. `error` is the 422
/// FieldError message keyed on this exact catalog path.
export default function FieldInput({
  spec,
  value,
  editable,
  error,
  canReveal,
  onReveal,
  onChange,
}: {
  spec: FieldSpec;
  value: string | undefined;
  editable: boolean;
  error?: string;
  canReveal?: boolean;
  onReveal?: () => void;
  onChange?: (path: string, value: string) => void;
}) {
  const masked = isMasked(value);
  const [editingMasked, setEditingMasked] = useState(false);
  const showInput = editable && (!masked || editingMasked);

  return (
    <Box sx={{ minWidth: 0 }}>
      {!showInput && (
        <Typography
          variant="caption"
          color={error ? "error" : "text.secondary"}
          sx={{ display: "block", mb: 0.25 }}
        >
          {spec.label}
          {spec.required ? " *" : ""}
        </Typography>
      )}
      {showInput ? (
        spec.enum || spec.type === "bool" ? (
          <TextField
            select
            size="small"
            fullWidth
            label={spec.label}
            required={spec.required}
            value={masked ? "" : (value ?? "")}
            error={!!error}
            helperText={error}
            onChange={(e) => onChange?.(spec.path, e.target.value)}
          >
            <MenuItem value="">
              <em>—</em>
            </MenuItem>
            {(spec.enum ?? ["true", "false"]).map((v) => (
              <MenuItem key={v} value={v}>
                {v === "true" ? "Yes" : v === "false" ? "No" : enumLabel(v)}
              </MenuItem>
            ))}
          </TextField>
        ) : (
          <TextField
            size="small"
            fullWidth
            label={spec.label}
            required={spec.required}
            value={masked ? "" : (value ?? "")}
            error={!!error}
            helperText={error}
            placeholder={masked ? "Enter new value" : undefined}
            type={spec.type === "date" ? "date" : "text"}
            slotProps={
              spec.type === "decimal" || spec.type === "int"
                ? { htmlInput: { inputMode: "decimal" } }
                : undefined
            }
            onChange={(e) => onChange?.(spec.path, e.target.value)}
          />
        )
      ) : (
        <MaskedValue
          value={value}
          masked={masked}
          canReveal={canReveal}
          onReveal={onReveal}
          onEdit={editable && masked ? () => setEditingMasked(true) : undefined}
        />
      )}
    </Box>
  );
}
