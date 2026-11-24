import "@testing-library/jest-dom";
import { fireEvent, render, renderHook, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";

import TolerancePanel from "@/components/TolerancePanel";
import { useRecordCure, useTolerance, type ToleranceReport } from "@/lib/closing";

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

const report = (over: Partial<ToleranceReport> = {}): ToleranceReport => ({
  versions: [
    {
      versionId: "v1",
      kind: "le",
      evalId: "e1",
      docId: "d1",
      seq: 1,
      resetOfVersionId: null,
      createdUtc: "2025-01-01T00:00:00Z",
    },
  ],
  resets: [],
  buckets: [
    {
      bucket: "Zero",
      baseline: 100,
      compare: 350,
      lines: [
        { feeCode: "discount-points", baseline: 100, compare: 350, delta: 250 },
      ],
    },
    {
      bucket: "Unlimited",
      baseline: 500,
      compare: 500,
      lines: [
        { feeCode: "transfer-tax", baseline: 500, compare: 500, delta: 0 },
      ],
    },
  ],
  violations: [],
  openCureConditionId: null,
  ...over,
});

describe("useTolerance", () => {
  it("maps the GraphQL tolerance report", async () => {
    const rep = report({
      violations: [
        {
          bucket: "Zero",
          feeCode: "discount-points",
          baseline: 100,
          compare: 350,
          allowed: 100,
          rule: "zero-tolerance",
        },
      ],
      openCureConditionId: "cond-9",
    });
    mockFetch(jsonResponse({ data: { tolerance: rep } }));
    const { result } = renderHook(() => useTolerance("demo", "app-1"), { wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.violations).toHaveLength(1);
    expect(result.current.data?.openCureConditionId).toBe("cond-9");
    expect(result.current.data?.buckets[0].lines[0].feeCode).toBe("discount-points");
  });

  it("surfaces null for non-staff scopes", async () => {
    mockFetch(jsonResponse({ data: { tolerance: null } }));
    const { result } = renderHook(() => useTolerance("demo", "app-1"), { wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toBeNull();
  });
});

describe("useRecordCure", () => {
  it("POSTs the cure reason to the tolerance endpoint", async () => {
    const fn = mockFetch(jsonResponse({ recorded: true }, 200));
    const { result } = renderHook(() => useRecordCure("demo", "app-1"), { wrapper });
    result.current.mutate({ reason: "borrower refunded at closing" });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    const [url, init] = fn.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/bff/api/tenants/demo/applications/app-1/tolerance/cure");
    expect(JSON.parse(init.body as string)).toEqual({
      reason: "borrower refunded at closing",
    });
  });
});

describe("TolerancePanel", () => {
  it("renders per-bucket variance and deltas", () => {
    render(<TolerancePanel report={report()} />);
    expect(screen.getByTestId("tolerance-bucket-Zero")).toBeInTheDocument();
    expect(screen.getByTestId("tolerance-bucket-Unlimited")).toBeInTheDocument();
    expect(screen.getByTestId("tolerance-delta-Zero")).toHaveTextContent("+$250.00");
    expect(screen.getByText(/0% — cannot increase/)).toBeInTheDocument();
  });

  it("shows the violation banner with the offending line", () => {
    render(
      <TolerancePanel
        report={report({
          violations: [
            {
              bucket: "Zero",
              feeCode: "discount-points",
              baseline: 100,
              compare: 350,
              allowed: 100,
              rule: "zero-tolerance",
            },
          ],
        })}
      />,
    );
    const banner = screen.getByTestId("tolerance-violation-banner");
    expect(banner).toHaveTextContent(/tolerance violation/i);
    expect(banner).toHaveTextContent(/discount-points/);
  });

  it("hides the cure affordance from non-authorized roles", () => {
    render(
      <TolerancePanel report={report({ openCureConditionId: "c1" })} canCure={false} />,
    );
    expect(screen.queryByTestId("record-cure-button")).not.toBeInTheDocument();
  });

  it("shows Record cure for authorized roles when a condition is open", () => {
    render(
      <TolerancePanel report={report({ openCureConditionId: "c1" })} canCure />,
    );
    expect(screen.getByTestId("record-cure-button")).toBeInTheDocument();
  });

  it("submits the recorded cure reason", () => {
    const onRecordCure = jest.fn();
    render(
      <TolerancePanel
        report={report({ openCureConditionId: "c1" })}
        canCure
        onRecordCure={onRecordCure}
      />,
    );
    fireEvent.click(screen.getByTestId("record-cure-button"));
    fireEvent.change(screen.getByTestId("cure-reason"), {
      target: { value: "cured at closing" },
    });
    fireEvent.click(screen.getByTestId("cure-submit"));
    expect(onRecordCure).toHaveBeenCalledWith("cured at closing");
  });
});
