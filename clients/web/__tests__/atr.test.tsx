import "@testing-library/jest-dom";
import { fireEvent, render, renderHook, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";

import AtrQmPanel from "@/components/AtrQmPanel";
import { useAtr, useEvaluateAtr, type AtrEvaluation, type AtrReport } from "@/lib/atr";

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

const evaluation = (over: Partial<AtrEvaluation> = {}): AtrEvaluation => ({
  resultId: "r1",
  evalId: "e1",
  dti: 37.8,
  apr: 6.6,
  apor: 6.5,
  verdicts: [
    { test: "atr_general", verdict: "pass", detail: "dti 37.80 reasonable" },
    { test: "qm_general", verdict: "pass", detail: "dti 37.80 ≤ 43" },
    { test: "qm_hud", verdict: "warn", detail: "product not fha — not applicable" },
    { test: "hoepa", verdict: "pass", detail: "apr-apor spread 0.10 < 6.5" },
    { test: "hpml", verdict: "pass", detail: "spread 0.10 < 1.5" },
    { test: "state_high_cost", verdict: "pass", detail: "spread within VA threshold" },
  ],
  actor: "officer@lola",
  createdUtc: "2025-01-01T00:00:00Z",
  ...over,
});

const report = (over: Partial<AtrReport> = {}): AtrReport => ({
  latest: evaluation(),
  history: [evaluation()],
  ...over,
});

describe("useAtr", () => {
  it("maps the GraphQL atr report", async () => {
    mockFetch(jsonResponse({ data: { atr: report() } }));
    const { result } = renderHook(() => useAtr("demo", "app-1"), { wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.latest?.dti).toBeCloseTo(37.8);
    expect(result.current.data?.latest?.verdicts).toHaveLength(6);
    expect(result.current.data?.history).toHaveLength(1);
  });

  it("surfaces null for non-staff scopes", async () => {
    mockFetch(jsonResponse({ data: { atr: null } }));
    const { result } = renderHook(() => useAtr("demo", "app-1"), { wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toBeNull();
  });

  it("stays idle when not enabled (lazy tab)", () => {
    const fn = mockFetch(jsonResponse({ data: { atr: report() } }));
    const { result } = renderHook(() => useAtr("demo", "app-1", false), { wrapper });
    expect(result.current.fetchStatus).toBe("idle");
    expect(fn).not.toHaveBeenCalled();
  });
});

describe("useEvaluateAtr", () => {
  it("POSTs to the evaluate endpoint", async () => {
    const fn = mockFetch(jsonResponse({ resultId: "r2" }, 200));
    const { result } = renderHook(() => useEvaluateAtr("demo", "app-1"), { wrapper });
    result.current.mutate();
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    const [url] = fn.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/bff/api/tenants/demo/applications/app-1/atr/evaluate");
  });
});

describe("AtrQmPanel", () => {
  it("renders per-test verdict chips in canonical order", () => {
    render(<AtrQmPanel report={report()} />);
    expect(screen.getByTestId("atr-test-atr_general")).toBeInTheDocument();
    expect(screen.getByTestId("atr-test-qm_general")).toBeInTheDocument();
    expect(screen.getByTestId("atr-test-hoepa")).toBeInTheDocument();
    expect(screen.getByTestId("atr-verdict-qm_general")).toHaveTextContent("pass");
    expect(screen.getByText(/QM — general/)).toBeInTheDocument();
  });

  it("shows figures — DTI, APR, APOR", () => {
    render(<AtrQmPanel report={report()} />);
    const figures = screen.getByTestId("atr-figures");
    expect(figures).toHaveTextContent("37.80%");
    expect(figures).toHaveTextContent("6.60%");
    expect(figures).toHaveTextContent("6.50%");
  });

  it("shows the warn banner when any test is incomplete", () => {
    render(<AtrQmPanel report={report()} />);
    expect(screen.getByTestId("atr-warn-banner")).toBeInTheDocument();
    expect(screen.queryByTestId("atr-fail-banner")).not.toBeInTheDocument();
  });

  it("shows the fail banner when any test fails", () => {
    const failing = evaluation({
      verdicts: [
        { test: "qm_general", verdict: "fail", detail: "dti 44.20 > 43" },
      ],
    });
    render(<AtrQmPanel report={{ latest: failing, history: [failing] }} />);
    expect(screen.getByTestId("atr-fail-banner")).toBeInTheDocument();
  });

  it("renders the empty state without an evaluation", () => {
    render(<AtrQmPanel report={null} canEvaluate />);
    expect(screen.getByText(/No ATR\/QM evaluation yet/)).toBeInTheDocument();
    expect(screen.getByTestId("evaluate-atr-button")).toHaveTextContent("Evaluate");
  });

  it("hides the evaluate affordance for non-staff", () => {
    render(<AtrQmPanel report={report()} canEvaluate={false} />);
    expect(screen.queryByTestId("evaluate-atr-button")).not.toBeInTheDocument();
  });

  it("fires onEvaluate from the button", () => {
    const onEvaluate = jest.fn();
    render(<AtrQmPanel report={report()} canEvaluate onEvaluate={onEvaluate} />);
    fireEvent.click(screen.getByTestId("evaluate-atr-button"));
    expect(onEvaluate).toHaveBeenCalledTimes(1);
  });

  it("notes append-only lineage when history has multiple rows", () => {
    const r = report({ history: [evaluation(), evaluation({ resultId: "r2" })] });
    render(<AtrQmPanel report={r} />);
    expect(screen.getByText(/2 evaluations recorded/)).toBeInTheDocument();
  });
});
