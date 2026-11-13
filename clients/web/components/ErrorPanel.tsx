import Alert from "@mui/material/Alert";
import AlertTitle from "@mui/material/AlertTitle";

/// Errors name the next action — the API's problem-details message is
/// shown verbatim; a generic fallback tells the operator what to try.
export default function ErrorPanel({
  title = "Something went wrong",
  error,
}: {
  title?: string;
  error: unknown;
}) {
  const message =
    error instanceof Error ? error.message : "Refresh the page; if the problem persists, contact an administrator.";
  return (
    <Alert severity="error" variant="outlined">
      <AlertTitle>{title}</AlertTitle>
      {message}
    </Alert>
  );
}
