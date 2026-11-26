import "@testing-library/jest-dom";
import { render, renderHook, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";

import RespaPanel from "@/components/RespaPanel";
import {
  useMarkNoticeReceived,
  useMarkNoticeSent,
  useRecordTransfer,
  useRespa,
  useRunEscrowAnalysis,
  type EscrowAnalysis,
  type RespaReport,
  type ServicingNotice,
} from "@/lib/respa";

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

const analysis = (over: Partial<EscrowAnalysis> = {}): EscrowAnalysis => ({
  analysisId: "esc-1",
  verdict: "shortage",
  annualDisbursements: 4800,
  cushionTarget: 800,
  lowPoint: 600,
  determinationAmount: 200,
  refundRequired: false,
  missingInputs: [],
  actor: "officer@lola",
  createdUtc: "2025-01-01T00:00:00Z",
  ...over,
});

const notice = (over: Partial<ServicingNotice> = {}): ServicingNotice => ({
  noticeId: "ntc-1",
  noticeKind: "transferor",
  transferEffective: "2025-03-20",
  transferorLabel: "Old Servicer",
  transfereeLabel: "New Servicer",
  sendBy: "2025-03-05",
  status: "pending",
  overdue: false,
  sentLate: false,
  sentUtc: null,
  receivedUtc: null,
  events: [{ status: "pending", actor: "officer@lola", createdUtc: "2025-01-01T00:00:00Z" }],
  ...over,
});

const report = (over: Partial<RespaReport> = {}): RespaReport => ({
  latestAnalysis: analysis(),
  analysisHistory: [analysis()],
  afbaDisclosureRequired: false,
  notices: [notice()],
  ...over,
});

describe("useRespa", () => {
  it("maps the GraphQL respa report", async () => {
    mockFetch(jsonResponse({ data: { respa: report() } }));
    const { result } = renderHook(() => useRespa("demo", "app-1"), { wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.latestAnalysis?.verdict).toBe("shortage");
    expect(result.current.data?.notices).toHaveLength(1);
    expect(result.current.data?.notices[0].sendBy).toBe("2025-03-05");
    expect(result.current.data?.afbaDisclosureRequired).toBe(false);
  });

  it("surfaces null for non-staff scopes", async () => {
    mockFetch(jsonResponse({ data: { respa: null } }));
    const { result } = renderHook(() => useRespa("demo", "app-1"), { wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toBeNull();
  });

  it("stays idle when not enabled (lazy tab)", () => {
    const fn = mockFetch(jsonResponse({ data: { respa: report() } }));
    const { result } = renderHook(() => useRespa("demo", "app-1", false), { wrapper });
    expect(result.current.fetchStatus).toBe("idle");
    expect(fn).not.toHaveBeenCalled();
  });
});

describe("mutation hooks", () => {
  it("useRunEscrowAnalysis POSTs to the analyze endpoint", async () => {
    const fn = mockFetch(jsonResponse({ analysisId: "esc-2" }, 200));
    const { result } = renderHook(() => useRunEscrowAnalysis("demo", "app-1"), { wrapper });
    result.current.mutate();
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    const [url] = fn.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/bff/api/tenants/demo/applications/app-1/respa/escrow/analyze");
  });

  it("useRecordTransfer POSTs labels and effective date", async () => {
    const fn = mockFetch(jsonResponse([], 200));
    const { result } = renderHook(() => useRecordTransfer("demo", "app-1"), { wrapper });
    result.current.mutate({
      transferEffective: "2025-03-20",
      transferorLabel: "Old",
      transfereeLabel: "New",
    });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    const [url, init] = fn.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/bff/api/tenants/demo/applications/app-1/respa/servicing/transfer");
    expect(JSON.parse(init.body as string)).toEqual({
      transferEffective: "2025-03-20",
      transferorLabel: "Old",
      transfereeLabel: "New",
    });
  });

  it("notice transitions POST to their notice-scoped endpoints", async () => {
    const fn = mockFetch(jsonResponse({}, 200));
    const sent = renderHook(() => useMarkNoticeSent("demo", "app-1"), { wrapper });
    sent.result.current.mutate("ntc-1");
    await waitFor(() => expect(sent.result.current.isSuccess).toBe(true));
    expect((fn.mock.calls[0] as [string])[0]).toBe(
      "/bff/api/tenants/demo/applications/app-1/respa/servicing/notices/ntc-1/sent",
    );

    const received = renderHook(() => useMarkNoticeReceived("demo", "app-1"), { wrapper });
    received.result.current.mutate("ntc-1");
    await waitFor(() => expect(received.result.current.isSuccess).toBe(true));
    expect((fn.mock.calls[1] as [string])[0]).toBe(
      "/bff/api/tenants/demo/applications/app-1/respa/servicing/notices/ntc-1/received",
    );
  });
});

describe("RespaPanel", () => {
  it("renders the verdict chip and figures", () => {
    render(<RespaPanel report={report()} />);
    expect(screen.getByTestId("escrow-verdict")).toHaveTextContent("shortage");
    const figures = screen.getByTestId("escrow-figures");
    expect(figures).toHaveTextContent("$4,800.00");
    expect(figures).toHaveTextContent("$800.00");
    expect(figures).toHaveTextContent("Shortage");
    expect(figures).toHaveTextContent("$200.00");
  });

  it("renders the refund-clock chip when the surplus crosses $50", () => {
    render(
      <RespaPanel
        report={report({
          latestAnalysis: analysis({ verdict: "surplus", refundRequired: true }),
        })}
      />,
    );
    expect(screen.getByTestId("refund-clock")).toBeInTheDocument();
  });

  it("renders incomplete distinctly with the missing inputs", () => {
    render(
      <RespaPanel
        report={report({
          latestAnalysis: analysis({
            verdict: "incomplete",
            annualDisbursements: null,
            cushionTarget: null,
            lowPoint: null,
            determinationAmount: null,
            missingInputs: ["escrow.monthly_payment"],
          }),
        })}
      />,
    );
    expect(screen.getByTestId("escrow-missing")).toHaveTextContent(
      "escrow.monthly_payment",
    );
    expect(screen.queryByTestId("escrow-figures")).not.toBeInTheDocument();
  });

  it("shows the AfBA banner only when triggered", () => {
    const { unmount } = render(<RespaPanel report={report()} />);
    expect(screen.queryByTestId("afba-banner")).not.toBeInTheDocument();
    unmount();
    render(<RespaPanel report={report({ afbaDisclosureRequired: true })} />);
    expect(screen.getByTestId("afba-banner")).toHaveTextContent("Section 8");
  });

  it("shows the overdue banner when a notice breaches", () => {
    render(
      <RespaPanel report={report({ notices: [notice({ overdue: true })] })} />,
    );
    expect(screen.getByTestId("overdue-banner")).toBeInTheDocument();
  });

  it("empty states render without blank chrome", () => {
    render(
      <RespaPanel
        report={{ latestAnalysis: null, analysisHistory: [], afbaDisclosureRequired: false, notices: [] }}
      />,
    );
    expect(screen.getByTestId("escrow-empty")).toBeInTheDocument();
    expect(screen.getByTestId("notices-empty")).toBeInTheDocument();
  });

  it("hides staff affordances without canAct", () => {
    render(<RespaPanel report={report()} />);
    expect(screen.queryByTestId("analyze-escrow-button")).not.toBeInTheDocument();
    expect(screen.queryByTestId("record-transfer-button")).not.toBeInTheDocument();
    expect(screen.queryByTestId("mark-sent-ntc-1")).not.toBeInTheDocument();
  });

  it("staff affordances fire their callbacks", () => {
    const onAnalyze = jest.fn();
    const onMarkSent = jest.fn();
    render(
      <RespaPanel
        report={report()}
        canAct
        onAnalyze={onAnalyze}
        onMarkSent={onMarkSent}
      />,
    );
    screen.getByTestId("analyze-escrow-button").click();
    expect(onAnalyze).toHaveBeenCalled();
    screen.getByTestId("mark-sent-ntc-1").click();
    expect(onMarkSent).toHaveBeenCalledWith("ntc-1");
  });

  it("mark-received only renders for sent notices", () => {
    render(
      <RespaPanel
        report={report({ notices: [notice({ status: "sent" })] })}
        canAct
        onMarkReceived={() => {}}
      />,
    );
    expect(screen.getByTestId("mark-received-ntc-1")).toBeInTheDocument();
    expect(screen.queryByTestId("mark-sent-ntc-1")).not.toBeInTheDocument();
  });
});
