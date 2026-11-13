import IconButton from "@mui/material/IconButton";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import Box from "@mui/material/Box";
import VisibilityIcon from "@mui/icons-material/Visibility";
import EditIcon from "@mui/icons-material/Edit";

import { isMasked } from "@/lib/doc";

/// A catalog leaf rendered masked-by-default ("•••"+last-4). The
/// reveal affordance is staff-only and routes through the audited
/// RevealDialog — the field itself never decrypts client-side.
export default function MaskedValue({
  value,
  masked,
  canReveal,
  onReveal,
  onEdit,
}: {
  value: string | undefined;
  masked?: boolean;
  canReveal?: boolean;
  onReveal?: () => void;
  onEdit?: () => void;
}) {
  const isMask = masked ?? isMasked(value);
  if (!isMask) {
    return (
      <Typography component="span" variant="body2">
        {value ?? "—"}
      </Typography>
    );
  }
  return (
    <Box component="span" sx={{ display: "inline-flex", alignItems: "center", gap: 0.5 }}>
      <Typography component="span" variant="body2" sx={{ fontFamily: "monospace" }}>
        {value ?? "•••"}
      </Typography>
      {canReveal && onReveal && (
        <Tooltip title="Reveal (audited)">
          <IconButton size="small" onClick={onReveal} aria-label="reveal">
            <VisibilityIcon sx={{ fontSize: 14 }} />
          </IconButton>
        </Tooltip>
      )}
      {onEdit && (
        <Tooltip title="Replace value">
          <IconButton size="small" onClick={onEdit} aria-label="replace">
            <EditIcon sx={{ fontSize: 14 }} />
          </IconButton>
        </Tooltip>
      )}
    </Box>
  );
}
