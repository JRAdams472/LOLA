import "@testing-library/jest-dom";
import { render, renderHook, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";

import HmdaPanel from "@/components/HmdaPanel";
import {
  exportUrl,
  useAssembleLar,
  useFreezeSnapshot,
  useHmda,
  type HmdaEdit,
  type HmdaLarRow,
  type HmdaReport,
} from "@/lib/hmda";

const fetchMock = () => {
  const fn = jest.fn();
  (globalThis as { fetch?: unknown }).fetch = fn;
  return fn;
};
const mockFetch = (value?: Response) => {
  const fn = fetchMock();
  return value === undefined ? fn : fn.mockResolvedValue(value);
};

const jsonResponse = (body: unknown, status = 200) =>
  ({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  }) as Response;

const wrapper = ({ children }: { children: ReactNode }) => (
  <QueryClientProvider
    client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
  >
    {children}
  </QueryClientProvider>
);

afterEach(() => jest.restoreAllMocks());

const edit = (over: Partial<HmdaEdit> = {}): HmdaEdit => ({
  editId: "V608",
  tier: "validity",
  result: "pass",
  detail: "",
  ...over,
});

const row = (over: Partial<HmdaLarRow> = {}): HmdaLarRow => ({
  larRowId: "lar-1",
  uli: "10BX939C5543TQA1144MAPP000000000138",
  reportingYear: 2026,
  fieldsJson: JSON.stringify({
    loan_amount: "300000",
    action_taken: "1",
    rate_spread: "1.50",
  }),
  edits: [
    edit({ editId: "S300", tier: "syntactical" }),
    edit({ editId: "V608", tier: "validity" }),
    edit({ editId: "V610", tier: "validity", result: "fail", detail: "bad date" }),
    edit({ editId: "Q600", tier: "quality", result: "warn", detail: "cltv odd" }),
  ],
  actor: "officer@lola",
  createdUtc: "2026-01-01T00:00:00Z",
  ...over,
});

const report = (over: Partial<HmdaReport> = {}): HmdaReport => ({
  latest: row(),
  lineage: [row(), row({ larRowId: "lar-0" })],
  figures: {
    actionTaken: "1",
    actionTakenDate: "20260415",
    rateSpread: "1.50",
    hoepaStatus: "2",
    failCount: 1,
    warnCount: 1,
  },
  ...over,
});

describe("HmdaPanel", () => {
  it("renders key figures with formatted values", () => {
    render(<HmdaPanel report={report()} />);
    expect(screen.getByTestId("hmda-uli")).toHaveTextContent("10BX939C5543TQA1144M");
    expect(screen.getByTestId("hmda-action")).toHaveTextContent("originated (2026-04-15)");
    expect(screen.getByTestId("hmda-spread")).toHaveTextContent("1.50 pp");
    expect(screen.getByTestId("hmda-hoepa")).toHaveTextContent("not high-cost");
    expect(screen.getByTestId("hmda-amount")).toHaveTextContent("$300,000");
  });

  it("groups edit results by tier with CFPB edit ids", () => {
    render(<HmdaPanel report={report()} />);
    expect(screen.getByTestId("tier-syntactical")).toBeInTheDocument();
    expect(screen.getByTestId("tier-validity")).toBeInTheDocument();
    expect(screen.getByTestId("tier-quality")).toBeInTheDocument();
    expect(screen.getByTestId("edit-V608")).toHaveTextContent("V608 pass");
    expect(screen.getByTestId("edit-V610")).toHaveTextContent("V610 fail");
    expect(screen.getByTestId("edit-Q600")).toHaveTextContent("Q600 warn");
  });

  it("shows the fail banner when validity edits fail", () => {
    render(<HmdaPanel report={report()} />);
    expect(screen.getByTestId("hmda-fail-banner")).toHaveTextContent("1 validity edit");
  });

  it("no fail banner when all edits pass", () => {
    const clean = report({
      figures: { actionTaken: "1", actionTakenDate: "20260415", rateSpread: "1.50", hoepaStatus: "2", failCount: 0, warnCount: 0 },
    });
    render(<HmdaPanel report={clean} />);
    expect(screen.queryByTestId("hmda-fail-banner")).not.toBeInTheDocument();
  });

  it("renders empty state when nothing assembled", () => {
    render(<HmdaPanel report={{ latest: null, lineage: [], figures: { actionTaken: null, actionTakenDate: null, rateSpread: null, hoepaStatus: null, failCount: 0, warnCount: 0 } }} />);
    expect(screen.getByTestId("hmda-empty")).toHaveTextContent("No assembled LAR row yet");
  });

  it("renders NA for missing figures — never invents values", () => {
    const na = report({
      figures: { actionTaken: null, actionTakenDate: null, rateSpread: null, hoepaStatus: null, failCount: 0, warnCount: 0 },
    });
    render(<HmdaPanel report={na} />);
    expect(screen.getByTestId("hmda-action")).toHaveTextContent("NA");
    expect(screen.getByTestId("hmda-spread")).toHaveTextContent("NA");
    expect(screen.getByTestId("hmda-hoepa")).toHaveTextContent("NA");
  });

  it("shows the append-only lineage count", () => {
    render(<HmdaPanel report={report()} />);
    expect(screen.getByTestId("hmda-lineage")).toHaveTextContent("append-only assembly lineage: 2 rows");
  });

  it("hides staff affordances when canAct is false", () => {
    render(<HmdaPanel report={report()} canAct={false} />);
    expect(screen.queryByTestId("assemble-btn")).not.toBeInTheDocument();
    expect(screen.queryByTestId("export-btn")).not.toBeInTheDocument();
  });

  it("shows staff affordances when canAct", () => {
    render(<HmdaPanel report={report()} canAct />);
    expect(screen.getByTestId("assemble-btn")).toHaveTextContent("Re-assemble LAR");
    expect(screen.getByTestId("export-btn")).toHaveTextContent("Export 2026");
    expect(screen.getByTestId("freeze-btn")).toBeInTheDocument();
  });

  it("assemble button reads Assemble LAR before first assembly", () => {
    render(
      <HmdaPanel
        report={{ latest: null, lineage: [], figures: { actionTaken: null, actionTakenDate: null, rateSpread: null, hoepaStatus: null, failCount: 0, warnCount: 0 } }}
        canAct
      />,
    );
    expect(screen.getByTestId("assemble-btn")).toHaveTextContent("Assemble LAR");
  });
});

describe("useHmda", () => {
  it("queries the hmda resolver with tenant + app id", async () => {
    const fetch = mockFetch(jsonResponse({ data: { hmda: report() } }));
    const { result } = renderHook(() => useHmda("t1", "app-1"), { wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.latest?.uli).toContain("10BX");
    const [, init] = fetch.mock.calls[0] as [string, { body: string }];
    expect(init.body).toContain("hmda(tenantId: $t, appId: $id)");
  });

  it("returns null data for non-staff resolver null", async () => {
    mockFetch(jsonResponse({ data: { hmda: null } }));
    const { result } = renderHook(() => useHmda("t1", "app-1"), { wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toBeNull();
  });
});

describe("useAssembleLar", () => {
  it("POSTs to the assemble endpoint", async () => {
    const fetch = mockFetch(jsonResponse(row()));
    const { result } = renderHook(() => useAssembleLar("t1", "app-1"), { wrapper });
    result.current.mutate();
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect((fetch.mock.calls[0] as [string])[0]).toBe(
      "/bff/api/tenants/t1/applications/app-1/hmda/assemble",
    );
  });
});

describe("export + freeze", () => {
  it("exportUrl shapes the download path", () => {
    expect(exportUrl("t1", 2026)).toBe("/api/tenants/t1/hmda/export?year=2026");
  });

  it("useFreezeSnapshot POSTs year as query param", async () => {
    const fetch = mockFetch(jsonResponse({ snapshotId: "snap-1" }));
    const { result } = renderHook(() => useFreezeSnapshot("t1"), { wrapper });
    result.current.mutate(2026);
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect((fetch.mock.calls[0] as [string])[0]).toBe("/bff/api/tenants/t1/hmda/freeze?year=2026");
  });
});
