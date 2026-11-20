import "@testing-library/jest-dom";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";

import DocumentsPage from "@/app/documents/page";
import { downloadUrl } from "@/lib/documents";

/**
 * Documents contract (MPE-223): upload goes multipart through the BFF
 * with the classification on the query string; friendly errors for
 * 413/415; status chips are borrower vocabulary; downloads use the
 * audited /download route (302 → capability URL, browser follows).
 */

jest.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(""),
}));

const ME = { sub: "u-b1", name: "Ada Lovelace", tid: "t1", roles: ["borrower"] };

const UPLOAD = (over: Partial<Record<string, unknown>> = {}) => ({
  vaultDocId: "v-1",
  classification: "paystub",
  statusLabel: "Received",
  statusKey: "received",
  filename: "stub.pdf",
  sizeBytes: 2048,
  uploadedUtc: "2026-01-04",
  ...over,
});

const LOAN = {
  id: "app-1",
  statusLabel: "Application in progress",
  statusStage: 0,
  statusStages: [],
  docJson: "{}",
  uploads: [
    UPLOAD(),
    UPLOAD({
      vaultDocId: "v-2",
      classification: "id",
      statusLabel: "We couldn't accept this file — please upload a different one",
      statusKey: "rejected",
      filename: "scan.exe",
      sizeBytes: 999,
    }),
    UPLOAD({
      vaultDocId: "v-3",
      classification: "w2",
      statusLabel: "Checking…",
      statusKey: "checking",
      filename: "w2.pdf",
      sizeBytes: 4096,
    }),
  ],
  documents: [
    {
      docId: "d-1",
      packageName: "Initial disclosures",
      kindLabel: "Loan Estimate package",
      ready: true,
      filename: "le.pdf",
      completedUtc: "2026-01-02",
    },
    {
      docId: "d-2",
      packageName: "Closing package",
      kindLabel: "Document package",
      ready: false,
      filename: null,
      completedUtc: null,
    },
  ],
};

const okRes = (data: unknown) =>
  Promise.resolve({
    ok: true,
    status: 200,
    json: () => Promise.resolve(data),
  } as unknown as Response);

const errRes = (status: number) =>
  Promise.resolve({
    ok: false,
    status,
    json: () => Promise.resolve({}),
  } as unknown as Response);

function mockFetch(
  opts: { uploadStatus?: number; conditions?: unknown } = {},
) {
  const calls: { url: string; init?: RequestInit }[] = [];
  global.fetch = jest.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input.toString();
    calls.push({ url, init });
    if (url === "/auth/me") return okRes(ME);
    if (url.endsWith("/conditions"))
      return okRes(opts.conditions ?? []);
    if (url === "/bff/graphql") {
      const body = JSON.parse(
        (init?.body as string) ?? "{}",
      ) as { query?: string };
      if (body.query?.includes("portalLoans"))
        return okRes({
          data: {
            portalLoans: [
              { id: "app-1", statusLabel: "x", statusStage: 0 },
            ],
          },
        });
      if (body.query?.includes("portalLoan"))
        return okRes({ data: { portalLoan: LOAN } });
      return okRes({});
    }
    if (url.includes("/uploads?classification="))
      return errRes(opts.uploadStatus ?? 202);
    return okRes({});
  }) as jest.Mock;
  return calls;
}

describe("documents page", () => {
  it("lists uploads with friendly status chips and disclosure downloads", async () => {
    mockFetch();
    render(<DocumentsPage />);

    // Friendly chips — never the raw vault vocabulary.
    expect(await screen.findByText("stub.pdf")).toBeInTheDocument();
    expect(screen.getByText("Received")).toBeInTheDocument();
    expect(
      screen.getByText(/couldn't accept this file/),
    ).toBeInTheDocument();
    expect(screen.getByText("Checking…")).toBeInTheDocument();
    expect(screen.queryByText(/quarantined/i)).not.toBeInTheDocument();

    // Received upload downloads via the audited vault route; rejected
    // offers retry; checking offers neither.
    const downloads = screen.getAllByRole("link", { name: "Download" });
    expect(downloads.map((d) => d.getAttribute("href"))).toEqual(
      expect.arrayContaining([
        "/bff/api/tenants/t1/applications/app-1/uploads/v-1/download",
        "/bff/api/tenants/t1/applications/app-1/documents/d-1/download",
      ]),
    );
    expect(
      screen.getByRole("button", { name: "Try again" }),
    ).toBeInTheDocument();

    // Ready disclosure downloads; pending one doesn't.
    expect(screen.getByText("Initial disclosures")).toBeInTheDocument();
    expect(screen.getByText("Loan Estimate package")).toBeInTheDocument();
    expect(screen.getByText("Not ready yet")).toBeInTheDocument();
  });

  it("uploads multipart with the classification on the query string", async () => {
    const calls = mockFetch();
    render(<DocumentsPage />);
    await screen.findByText("stub.pdf");

    const file = new File(["%PDF-x"], "pay.pdf", {
      type: "application/pdf",
    });
    fireEvent.change(screen.getByLabelText("File"), {
      target: { files: [file] },
    });
    fireEvent.click(screen.getByRole("button", { name: "Upload" }));

    await waitFor(() =>
      expect(
        screen.getByText(/Received — we're checking it now/),
      ).toBeInTheDocument(),
    );
    const up = calls.find((c) => c.url.includes("/uploads?"));
    expect(up?.url).toBe(
      "/bff/api/tenants/t1/applications/app-1/uploads?classification=paystub",
    );
    expect(up?.init?.method).toBe("POST");
    expect(up?.init?.body).toBeInstanceOf(FormData);
  });

  it("lists open conditions in borrower language, closed ones hidden", async () => {
    mockFetch({
      conditions: [
        {
          conditionId: "c-1",
          kind: "doc_required",
          state: "open",
          blocking: true,
          title: "Replace document — expired_doc",
          openedUtc: "2026-01-05",
          closedUtc: null,
        },
        {
          conditionId: "c-2",
          kind: "distance_check",
          state: "open",
          blocking: false,
          title: "Review — employer locality differs from subject property",
          openedUtc: "2026-01-05",
          closedUtc: null,
        },
        {
          conditionId: "c-3",
          kind: "doc_required",
          state: "closed",
          blocking: true,
          title: "Replace document — unreadable",
          openedUtc: "2026-01-01",
          closedUtc: "2026-01-04",
        },
      ],
    });
    render(<DocumentsPage />);

    expect(await screen.findByText("What we need")).toBeInTheDocument();
    expect(
      screen.getByText(/send a new copy — it looks out of date/),
    ).toBeInTheDocument();
    expect(screen.getByText("Needed from you")).toBeInTheDocument();
    expect(
      screen.getByText(/where you work versus the home's location/),
    ).toBeInTheDocument();
    expect(screen.getByText("In review")).toBeInTheDocument();
    // The closed condition never renders.
    expect(screen.queryByText(/couldn't read it/)).not.toBeInTheDocument();
  });

  it.each([
    [413, /too large/],
    [415, /can't read that file type/],
    [500, /didn't go through/],
  ])("surfaces a friendly message on %i", async (status, pattern) => {
    mockFetch({ uploadStatus: status });
    render(<DocumentsPage />);
    await screen.findByText("stub.pdf");

    fireEvent.change(screen.getByLabelText("File"), {
      target: { files: [new File(["x"], "a.pdf")] },
    });
    fireEvent.click(screen.getByRole("button", { name: "Upload" }));

    await waitFor(() =>
      expect(screen.getByRole("alert")).toHaveTextContent(pattern),
    );
  });
});

describe("downloadUrl", () => {
  it("builds the audited BFF download route for docs and vault", () => {
    expect(downloadUrl("t1", "app-1", "d-1")).toBe(
      "/bff/api/tenants/t1/applications/app-1/documents/d-1/download",
    );
    expect(downloadUrl("t1", "app-1", "v-1", true)).toBe(
      "/bff/api/tenants/t1/applications/app-1/uploads/v-1/download",
    );
  });
});
