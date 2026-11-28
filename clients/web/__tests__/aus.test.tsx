import "@testing-library/jest-dom";
import { render, renderHook, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";

import AusPanel from "@/components/AusPanel";
import {
  useAus,
  useSubmitAus,
  type AusFinding,
  type AusReport,
  type AusVendorPosture,
} from "@/lib/aus";

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

const finding = (over: Partial<AusFinding> = {}): AusFinding => ({
  vendorCode: "DU-1201",
  category: "credit",
  severity: "warning",
  message: "Credit score below threshold",
  conditionId: null,
  ...over,
});

const posture = (over: Partial<AusVendorPosture> = {}): AusVendorPosture => ({
  vendor: "du",
  recommendation: "approve-eligible",
  riskClass: null,
  caseId: "DU-CASE-1",
  createdUtc: "2026-01-01T00:00:00Z",
  findingsCount: 1,
  findings: [finding()],
  ...over,
});

const report = (over: Partial<AusReport> = {}): AusReport => ({
  vendors: [posture()],
  lineage: [
    {
      resultId: "aus-1",
      vendor: "du",
      recommendation: "approve-eligible",
      createdUtc: "2026-01-01T00:00:00Z",
      findingsCount: 1,
    },
  ],
  ...over,
});

describe("AusPanel", () => {
  it("renders the vendor recommendation chip", () => {
    render(<AusPanel report={report()} />);
    expect(screen.getByTestId("rec-du")).toHaveTextContent("approve-eligible");
    expect(screen.getByTestId("vendor-du")).toHaveTextContent(
      "Desktop Underwriter",
    );
  });

  it("renders findings with severity chips", () => {
    render(<AusPanel report={report()} />);
    expect(screen.getByTestId("finding-DU-1201")).toBeInTheDocument();
  });

  it("renders the risk class when present", () => {
    render(
      <AusPanel
        report={report({
          vendors: [posture({ riskClass: "Accept" })],
        })}
      />,
    );
    expect(screen.getByTestId("risk-du")).toHaveTextContent("risk: Accept");
  });

  it("shows the blocking banner for required/fatal findings", () => {
    render(
      <AusPanel
        report={report({
          vendors: [
            posture({
              findings: [
                finding({ severity: "required", conditionId: "cond-1" }),
              ],
            }),
          ],
        })}
      />,
    );
    expect(screen.getByTestId("aus-blocking-banner")).toHaveTextContent(
      "conditions have been opened",
    );
  });

  it("renders no banner for warning-only findings", () => {
    render(<AusPanel report={report()} />);
    expect(screen.queryByTestId("aus-blocking-banner")).not.toBeInTheDocument();
  });

  it("renders a clean empty state when nothing submitted", () => {
    render(<AusPanel report={report({ vendors: [], lineage: [] })} />);
    expect(screen.getByTestId("aus-empty")).toHaveTextContent(
      "No automated underwriting submissions yet",
    );
  });

  it("renders the lineage count", () => {
    render(<AusPanel report={report()} />);
    expect(screen.getByTestId("aus-lineage")).toHaveTextContent(
      "1 submission on record",
    );
  });

  it("hides submit affordances for non-staff", () => {
    render(<AusPanel report={report()} canAct={false} />);
    expect(screen.queryByTestId("submit-du")).not.toBeInTheDocument();
    expect(screen.queryByTestId("submit-lpa")).not.toBeInTheDocument();
  });

  it("submit buttons invoke the vendor picker", () => {
    const onSubmit = jest.fn();
    render(<AusPanel report={report()} canAct onSubmit={onSubmit} />);
    screen.getByTestId("submit-du").click();
    expect(onSubmit).toHaveBeenCalledWith("du");
    screen.getByTestId("submit-lpa").click();
    expect(onSubmit).toHaveBeenCalledWith("lpa");
  });

  it("submit buttons disable while pending", () => {
    render(<AusPanel report={report()} canAct submitPending />);
    expect(screen.getByTestId("submit-du")).toBeDisabled();
    expect(screen.getByTestId("submit-lpa")).toBeDisabled();
  });

  it("renders both vendors when both submitted", () => {
    render(
      <AusPanel
        report={report({
          vendors: [
            posture(),
            posture({
              vendor: "lpa",
              recommendation: "caution",
              findings: [],
            }),
          ],
        })}
      />,
    );
    expect(screen.getByTestId("vendor-du")).toBeInTheDocument();
    expect(screen.getByTestId("vendor-lpa")).toBeInTheDocument();
    expect(screen.getByTestId("rec-lpa")).toHaveTextContent("caution");
  });
});

describe("useAus", () => {
  it("queries the aus resolver with tenant + app variables", async () => {
    const fetch = mockFetch(
      jsonResponse({ data: { aus: report() } }),
    );
    const { result } = renderHook(() => useAus("t1", "app-1"), { wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.vendors[0].recommendation).toBe(
      "approve-eligible",
    );
    const body = JSON.parse((fetch.mock.calls[0][1] as RequestInit).body as string);
    expect(body.query).toContain("aus(tenantId: $t, appId: $id)");
    expect(body.variables).toEqual({ t: "t1", id: "app-1" });
  });

  it("does not fetch when tenant is missing", () => {
    const fetch = mockFetch();
    renderHook(() => useAus(null, "app-1"), { wrapper });
    expect(fetch).not.toHaveBeenCalled();
  });
});

describe("useSubmitAus", () => {
  it("posts to the vendor-picked submit endpoint", async () => {
    const fetch = mockFetch(jsonResponse({ ok: true }));
    const { result } = renderHook(() => useSubmitAus("t1", "app-1"), {
      wrapper,
    });
    result.current.mutate("lpa");
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining("/aus/submit?vendor=lpa"),
      expect.anything(),
    );
  });
});
