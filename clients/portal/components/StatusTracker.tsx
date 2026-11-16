import type { PortalStage } from "@/lib/loans";

/**
 * Three-stage borrower tracker — done → current → upcoming. Stages come
 * from the server (BorrowerStatusMap), so internal pipeline vocabulary
 * can never render here.
 */
export default function StatusTracker({
  stages,
}: {
  stages: PortalStage[];
}) {
  return (
    <ol
      aria-label="Application progress"
      className="mt-4 flex items-center gap-0"
    >
      {stages.map((s, i) => (
        <li key={s.key} className="flex flex-1 items-center">
          <div className="flex flex-1 flex-col items-center gap-1.5">
            <span
              aria-current={s.current ? "step" : undefined}
              className={`flex h-6 w-6 items-center justify-center rounded-full border text-[11px] ${
                s.done
                  ? "border-accent bg-accent/15 text-accent"
                  : s.current
                    ? "border-accent bg-accent text-white"
                    : "border-border bg-surface-2 text-muted"
              }`}
            >
              {s.done ? "✓" : i + 1}
            </span>
            <span
              className={`text-center text-[11px] leading-tight ${
                s.current ? "font-medium text-text" : "text-muted"
              }`}
            >
              {s.label}
            </span>
          </div>
          {i < stages.length - 1 && (
            <div
              aria-hidden
              className={`-mt-5 h-px flex-1 ${
                s.done ? "bg-accent" : "bg-border"
              }`}
            />
          )}
        </li>
      ))}
    </ol>
  );
}
