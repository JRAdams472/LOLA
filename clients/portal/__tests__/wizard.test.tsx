import "@testing-library/jest-dom";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";

import ApplyPage from "@/app/apply/page";
import {
  displayValue,
  errorsBySection,
  patchFor,
  type WizardState,
} from "@/lib/apply";

/**
 * Wizard contract (MPE-222): masked PII confirms as "•••"+last-4 and
 * never round-trips into a PATCH; drafts save on demand and nav;
 * submit 422s deep-link to the owning section.
 */

type Ready = Extract<WizardState, { kind: "ready" }>;

const readyState = (over: Partial<Ready> = {}): Ready => ({
  kind: "ready",
  appId: "app-1",
  doc: {
    party: [
      { ssn: "•••6789", first_name: "Ada" },
    ],
    loan_amount: "425000",
  },
  edits: {},
  dirty: false,
  saving: false,
  fieldErrors: {},
  submitErrors: [],
  ...over,
});

describe("patchFor — the masked-confirm PATCH contract", () => {
  it("never echoes a masked saved value back", () => {
    const s = readyState({
      edits: { "party[0].ssn": "", "loan_amount": "500000" },
    });
    const patch = patchFor(s);
    expect(patch["party[0].ssn"]).toBeUndefined(); // opened, didn't retype
    expect(patch["loan_amount"]).toBe("500000");
    expect(JSON.stringify(patch)).not.toContain("•••");
  });

  it("sends a typed replacement for a masked field", () => {
    const s = readyState({ edits: { "party[0].ssn": "000987654" } });
    expect(patchFor(s)["party[0].ssn"]).toBe("000987654");
  });

  it("drops no-op edits and allows clearing non-PII fields", () => {
    const s = readyState({
      edits: {
        "loan_amount": "425000",          // same as saved — no-op
        "party[0].first_name": "",        // clearing plaintext field is real
      },
    });
    const patch = patchFor(s);
    expect(patch["loan_amount"]).toBeUndefined();
    expect(patch["party[0].first_name"]).toBe("");
  });

  it("returns empty when nothing changed", () => {
    expect(patchFor(readyState())).toEqual({});
  });
});

describe("errorsBySection — 422 → wizard sections", () => {
  it("groups errors by the field map and buckets unknowns", () => {
    const groups = errorsBySection([
      { path: "loan_amount", code: "required", message: "Required" },
      { path: "party[0].ssn", code: "format", message: "9 digits" },
      { path: "no_such_field", code: "x", message: "mystery" },
    ]);
    const byId = Object.fromEntries(
      groups.map((g) => [g.section, g.errors.length]),
    );
    expect(byId.borrower).toBe(1);
    expect(byId.loan).toBe(2); // loan_amount + unknown fallback
  });
});

describe("displayValue", () => {
  it("prefers a typed edit over the saved doc value", () => {
    const s = readyState({ edits: { loan_amount: "1" } });
    expect(displayValue(s, "loan_amount")).toBe("1");
    expect(displayValue(s, "party[0].ssn")).toBe("•••6789");
  });
});

// ---- page-level: resume + save + submit on mocked wire ----

const ME = { sub: "u-b1", name: "Ada", tid: "t1", roles: ["borrower"] };

const DRAFT_DOC = {
  party: [{ role: "borrower", first_name: "Ada", ssn: "•••6789" }],
};

const okRes = (data: unknown) =>
  Promise.resolve({
    ok: true,
    status: 200,
    json: () => Promise.resolve(data),
  } as Response);

function mockWizardFetch(opts: {
  doc?: object;
  submitStatus?: number;
  submitBody?: unknown;
  /** Post-submit stage — the initial load is always a draft. */
  afterSubmitStage?: number;
}) {
  const calls: { url: string; method: string; body?: string }[] = [];
  let submitted = false;
  const stage = () =>
    submitted ? (opts.afterSubmitStage ?? 1) : 0;
  global.fetch = jest.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input.toString();
    calls.push({ url, method: init?.method ?? "GET", body: init?.body as string });
    if (url === "/auth/me") return okRes(ME);
    if (url === "/bff/graphql") {
      const q = JSON.parse((init?.body as string) ?? "{}") as {
        query?: string;
      };
      if (q.query?.includes("portalLoans"))
        return okRes({
          data: {
            portalLoans: [
              { id: "app-1", statusStage: stage() },
            ],
          },
        });
      if (q.query?.includes("portalLoan"))
        return okRes({
          data: {
            portalLoan: {
              id: "app-1",
              statusLabel: stage() === 0
                ? "Application in progress" : "Under review",
              statusStage: stage(),
              statusStages: [],
              docJson: JSON.stringify(opts.doc ?? DRAFT_DOC),
              uploads: [],
              documents: [],
            },
          },
        });
      return okRes({});
    }
    if (url.endsWith("/submit")) {
      submitted = (opts.submitStatus ?? 200) < 300;
      return Promise.resolve({
        ok: (opts.submitStatus ?? 200) < 300,
        status: opts.submitStatus ?? 200,
        json: () => Promise.resolve(opts.submitBody ?? {}),
      } as Response);
    }
    if (init?.method === "PATCH") return okRes({ version: 2 });
    if (init?.method === "POST") return okRes({ id: "app-1" });
    return okRes({});
  }) as jest.Mock;
  return calls;
}

afterEach(() => jest.restoreAllMocks());

test("resume shows masked PII and never puts the mask in a PATCH", async () => {
  const calls = mockWizardFetch({});
  render(<ApplyPage />);

  // Resume: the masked SSN renders as confirm text with a Change action.
  const masked = await screen.findByText("•••6789");
  expect(masked).toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Change" }));

  const ssn = await screen.findByPlaceholderText("Enter new value");
  expect(ssn).toHaveValue("");
  fireEvent.change(ssn, { target: { value: "000987654" } });
  fireEvent.change(await screen.findByLabelText(/First Name/), {
    target: { value: "Marina" },
  });

  fireEvent.click(screen.getByRole("button", { name: "Save draft" }));

  await waitFor(() => {
    const patch = calls.find((c) => c.method === "PATCH");
    expect(patch).toBeDefined();
    const body = JSON.parse(patch!.body!) as { patch: Record<string, string> };
    expect(body.patch["party[0].ssn"]).toBe("000987654");
    expect(body.patch["party[0].first_name"]).toBe("Marina");
    expect(patch!.body).not.toContain("•••");
  });
});

test("submit 422 deep-links to the section owning the field error", async () => {
  mockWizardFetch({
    submitStatus: 422,
    submitBody: {
      title: "validation failed",
      errors: [
        { path: "loan_amount", code: "required", message: "Loan amount is required" },
      ],
    },
  });
  render(<ApplyPage />);

  // Jump straight to Review and submit the incomplete draft.
  fireEvent.click(await screen.findByRole("button", { name: "Review" }));
  fireEvent.click(
    await screen.findByRole("button", { name: "Submit application" }),
  );

  // The wizard lands on "Property & loan" with the inline field error.
  await waitFor(() =>
    expect(
      screen.getByText("Loan amount is required"),
    ).toBeInTheDocument(),
  );
  expect(
    screen.getByRole("button", { name: "Property & loan" }),
  ).toHaveAttribute("aria-current", "step");
});

test("no draft offers Begin, which creates one and opens the wizard", async () => {
  let created = false;
  global.fetch = jest.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input.toString();
    if (url === "/auth/me") return okRes(ME);
    if (url === "/bff/graphql") {
      const q = JSON.parse((init?.body as string) ?? "{}") as { query?: string };
      if (q.query?.includes("portalLoans"))
        return okRes({
          data: {
            portalLoans: created ? [{ id: "app-new", statusStage: 0 }] : [],
          },
        });
      if (q.query?.includes("portalLoan"))
        return okRes({
          data: {
            portalLoan: {
              id: "app-new", statusLabel: "Application in progress",
              statusStage: 0, statusStages: [],
              docJson: "{}", uploads: [], documents: [],
            },
          },
        });
      return okRes({});
    }
    if (init?.method === "POST") {
      created = true;
      return okRes({ id: "app-new" });
    }
    return okRes({});
  }) as jest.Mock;
  render(<ApplyPage />);

  fireEvent.click(
    await screen.findByRole("button", { name: "Begin" }),
  );
  // The created draft loads the wizard — borrower section fields render.
  await waitFor(() =>
    expect(screen.getByLabelText(/First Name/)).toBeInTheDocument(),
  );
});

test("a submitted application shows the locked card, not the wizard", async () => {
  global.fetch = jest.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input.toString();
    if (url === "/auth/me") return okRes(ME);
    if (url === "/bff/graphql") {
      const q = JSON.parse((init?.body as string) ?? "{}") as { query?: string };
      if (q.query?.includes("portalLoans"))
        return okRes({
          data: { portalLoans: [{ id: "app-1", statusStage: 1 }] },
        });
      if (q.query?.includes("portalLoan"))
        return okRes({
          data: {
            portalLoan: {
              id: "app-1", statusLabel: "Under review", statusStage: 1,
              statusStages: [], docJson: "{}", uploads: [], documents: [],
            },
          },
        });
      return okRes({});
    }
    return okRes({});
  }) as jest.Mock;
  render(<ApplyPage />);

  await waitFor(() =>
    expect(screen.getByText("Under review")).toBeInTheDocument(),
  );
  expect(
    screen.getByText(/with your loan officer/),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Submit application" }),
  ).not.toBeInTheDocument();
});

test("happy submit shows the success handoff", async () => {
  mockWizardFetch({ afterSubmitStage: 1 }); // post-submit reload → under review
  render(<ApplyPage />);

  fireEvent.click(await screen.findByRole("button", { name: "Review" }));
  fireEvent.click(
    await screen.findByRole("button", { name: "Submit application" }),
  );

  await waitFor(() =>
    expect(
      screen.getByText("Application submitted"),
    ).toBeInTheDocument(),
  );
});
