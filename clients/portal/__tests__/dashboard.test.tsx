import "@testing-library/jest-dom";
import { render, screen, waitFor } from "@testing-library/react";

import HomePage from "@/app/page";
import { maskedName, nextActionFor, type PortalLoan } from "@/lib/loans";

/**
 * Dashboard contract (MPE-221): the borrower sees their own loan with
 * friendly status vocabulary and masked PII — the page never renders
 * internal fields because the response type doesn't carry them.
 */

const mockReplace = jest.fn();
jest.mock("next/navigation", () => ({
  useRouter: () => ({ replace: mockReplace }),
}));

const ME = { sub: "u-b1", name: "Ada Lovelace", tid: "t1", roles: ["borrower"] };

const LOAN_META = {
  id: "app-1",
  statusLabel: "Application in progress",
  statusStage: 0,
};

const LOAN_FULL = {
  ...LOAN_META,
  statusStages: [
    { key: "applying", label: "Your application", done: false, current: true },
    { key: "review", label: "Under review", done: false, current: false },
    { key: "decision", label: "Decision", done: false, current: false },
  ],
  docJson: JSON.stringify({
    party: [
      {
        first_name: "•••a",
        last_name: "•••lace",
        ssn: "•••6789",
      },
    ],
  }),
  uploads: [
    {
      vaultDocId: "v-1",
      classification: "paystub",
      statusLabel: "Received",
      statusKey: "received",
      filename: "stub.pdf",
      sizeBytes: 1234,
      uploadedUtc: "2026-01-04",
    },
  ],
  documents: [
    {
      docId: "d-1",
      packageName: "Initial LE",
      kindLabel: "Loan Estimate package",
      ready: true,
      filename: "le.pdf",
      completedUtc: "2026-01-02",
    },
  ],
};

const okRes = (data: unknown) =>
  Promise.resolve({
    ok: true,
    status: 200,
    json: () => Promise.resolve(data),
  } as Response);

function mockFetch(routes: Record<string, unknown>) {
  global.fetch = jest.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input.toString();
    if (url === "/auth/me") return okRes(ME);
    // /bff/graphql — dispatch on the query's root field.
    const body = JSON.parse(
      (init?.body as string) ?? "{}",
    ) as { query?: string };
    if (body.query?.includes("portalLoans"))
      return okRes({ data: { portalLoans: routes.portalLoans } });
    if (body.query?.includes("portalLoan"))
      return okRes({ data: { portalLoan: routes.portalLoan } });
    return okRes({});
  }) as jest.Mock;
}

afterEach(() => jest.restoreAllMocks());

test("dashboard renders the borrower's loan with friendly status and masked PII", async () => {
  mockFetch({ portalLoans: [LOAN_META], portalLoan: LOAN_FULL });
  render(<HomePage />);

  await waitFor(() =>
    expect(
      screen.getByText("Application in progress"),
    ).toBeInTheDocument(),
  );

  // Masked-confirm: the SSN never renders plaintext; the name shows
  // what the server allowed (masked last name only).
  expect(screen.queryByText(/1234/)).not.toBeInTheDocument();
  expect(screen.getByText(/•••lace/)).toBeInTheDocument();

  // Friendly tracker vocabulary — never the internal step names.
  expect(screen.getByText("Under review")).toBeInTheDocument();
  expect(screen.queryByText(/POSIntake|PPE/i)).not.toBeInTheDocument();

  // Progress + summaries.
  expect(
    screen.getByText("Finish and submit your application."),
  ).toBeInTheDocument();
  expect(screen.getByText("1 ready")).toBeInTheDocument();
  expect(screen.getByText("1 received")).toBeInTheDocument();
});

test("dashboard shows the empty state when no application exists", async () => {
  mockFetch({ portalLoans: [], portalLoan: null });
  render(<HomePage />);

  await waitFor(() =>
    expect(
      screen.getByText(/When your loan officer starts your application/),
    ).toBeInTheDocument(),
  );
});

test("dashboard shows an error state when the read fails", async () => {
  global.fetch = jest.fn((input: RequestInfo | URL) => {
    const url = typeof input === "string" ? input : input.toString();
    if (url === "/auth/me") return okRes(ME);
    return okRes({ errors: [{ message: "boom" }] });
  }) as jest.Mock;
  render(<HomePage />);

  await waitFor(() =>
    expect(
      screen.getByRole("alert"),
    ).toHaveTextContent(/couldn't load your application/i),
  );
});

test("partner roles are redirected to /partner, never the dashboard", async () => {
  global.fetch = jest.fn((input: RequestInfo | URL) => {
    const url = typeof input === "string" ? input : input.toString();
    if (url === "/auth/me") return okRes({ ...ME, roles: ["Broker"] });
    return okRes({});
  }) as jest.Mock;
  render(<HomePage />);

  await waitFor(() => expect(mockReplace).toHaveBeenCalledWith("/partner"));
  expect(
    screen.queryByText(/When your loan officer starts your application/),
  ).not.toBeInTheDocument();
});

test("maskedName composes only what the doc carries", () => {
  expect(maskedName(LOAN_FULL.docJson)).toBe("•••a •••lace");
  expect(maskedName("{}")).toBeNull();
  expect(maskedName("not-json")).toBeNull();
});

test("nextActionFor flags a rejected upload on any stage", () => {
  const rejected: PortalLoan = {
    ...LOAN_FULL,
    statusStage: 1,
    uploads: [{ ...LOAN_FULL.uploads[0], statusKey: "rejected" }],
  };
  expect(nextActionFor(rejected)).toMatch(/needs attention/);
  expect(nextActionFor({ ...LOAN_FULL, statusStage: 2 })).toMatch(
    /decision is ready/,
  );
});
