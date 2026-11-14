import "@testing-library/jest-dom";
import { renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";

import {
  downloadUrl,
  useDocuments,
  useEvaluateFees,
  useFees,
  useLockRate,
  useOverrideFee,
  usePrice,
  usePricing,
  useRateLock,
  useRequestPackage,
  useUnlockRate,
  useUploads,
} from "@/lib/closing";
import {
  useAddMember,
  useCreateGroup,
  useCreateInvite,
  useDeleteGroup,
  useGroupMembers,
  useRemoveMember,
  useRetention,
  useTenantAudit,
  useTenantGroups,
  useTenantUsers,
  useUpdateGroup,
} from "@/lib/admin";

const fetchMock = () => {
  const fn = jest.fn();
  (globalThis as { fetch?: unknown }).fetch = fn;
  return fn;
};
const mockFetch = (value?: Response) => {
  const fn = fetchMock();
  return value === undefined ? fn : fn.mockResolvedValue(value);
};

// jsdom has no fetch/Response — a response-shaped stub is enough.
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

describe("closing reads", () => {
  it("usePricing maps the latest eval with products", async () => {
    mockFetch(
      jsonResponse({
        data: {
          pricing: {
            evalId: "ev1",
            status: "done",
            result: {
              products: [
                {
                  productCode: "CNV30",
                  productName: "Conventional 30",
                  eligible: true,
                  ineligibilityReasons: [],
                  offers: [{ rate: 6.5, price: 99.5, margin: 0.25, lockTermDays: 30, investorName: "FNMA" }],
                },
              ],
            },
          },
        },
      }),
    );
    const { result } = renderHook(() => usePricing("demo", "app-1"), { wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.result?.products[0].offers[0].rate).toBe(6.5);
  });

  it("useFees maps sections and stale overrides", async () => {
    mockFetch(
      jsonResponse({
        data: {
          fees: {
            batchId: "b1",
            evals: [
              {
                evalId: "fe1",
                status: "done",
                computedTotal: 5000,
                effectiveTotal: 4500,
                sections: [
                  {
                    section: "A",
                    computedTotal: 1500,
                    effectiveTotal: 1000,
                    items: [
                      {
                        itemId: "i1",
                        feeCode: "ORIG",
                        label: "Origination",
                        base: 1500,
                        computed: 1500,
                        overrideAmount: 1000,
                        overrideReason: "relationship pricing",
                        effective: 1000,
                      },
                    ],
                  },
                ],
                staleOverrides: [
                  {
                    overrideId: "o1",
                    feeCode: "GONE",
                    section: "B",
                    amount: 50,
                    reason: "waived",
                    actor: "officer",
                    createdUtc: "2026-01-01T00:00:00Z",
                    variant: "",
                  },
                ],
              },
            ],
          },
        },
      }),
    );
    const { result } = renderHook(() => useFees("demo", "app-1"), { wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    const eval0 = result.current.data?.evals[0];
    expect(eval0?.sections[0].items[0].overrideAmount).toBe(1000);
    expect(eval0?.staleOverrides).toHaveLength(1);
  });

  it("useRateLock/useDocuments/useUploads map their rows", async () => {
    const spy = mockFetch();
    spy.mockResolvedValueOnce(
      jsonResponse({
        data: { rateLock: { lockId: "l1", status: "active", rate: 6.5 } },
      }),
    )
      .mockResolvedValueOnce(
        jsonResponse({
          data: { documents: [{ docId: "d1", status: "completed", packageName: "le" }] },
        }),
      )
      .mockResolvedValueOnce(
        jsonResponse({
          data: { uploads: [{ vaultDocId: "v1", status: "clean", filename: "paystub.pdf" }] },
        }),
      );
    const lock = renderHook(() => useRateLock("demo", "app-1"), { wrapper });
    const docs = renderHook(() => useDocuments("demo", "app-1"), { wrapper });
    const uploads = renderHook(() => useUploads("demo", "app-1"), { wrapper });
    await waitFor(() => expect(lock.result.current.isSuccess).toBe(true));
    await waitFor(() => expect(docs.result.current.isSuccess).toBe(true));
    await waitFor(() => expect(uploads.result.current.isSuccess).toBe(true));
    expect(lock.result.current.data?.status).toBe("active");
    expect(docs.result.current.data?.[0].packageName).toBe("le");
    expect(uploads.result.current.data?.[0].filename).toBe("paystub.pdf");
  });

  it("downloadUrl builds the BFF download path", () => {
    expect(downloadUrl("demo", "app-1", "d1")).toBe(
      "/bff/api/tenants/demo/applications/app-1/documents/d1/download",
    );
    expect(downloadUrl("demo", "app-1", "v1", true)).toBe(
      "/bff/api/tenants/demo/applications/app-1/uploads/v1/download",
    );
  });
});

describe("closing mutations", () => {
  it("usePrice POSTs the enqueue endpoint", async () => {
    const spy = mockFetch(jsonResponse({ evalId: "ev1", deduplicated: false }, 202));
    const { result } = renderHook(() => usePrice("demo", "app-1"), { wrapper });
    result.current.mutate();
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(spy.mock.calls[0][0]).toBe("/bff/api/tenants/demo/applications/app-1/price");
    expect(result.current.data?.status).toBe(202);
  });

  it("useEvaluateFees POSTs the enqueue endpoint", async () => {
    const spy = mockFetch(jsonResponse({ evalId: "fe1", deduplicated: false }, 202));
    const { result } = renderHook(() => useEvaluateFees("demo", "app-1"), { wrapper });
    result.current.mutate();
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(spy.mock.calls[0][0]).toBe("/bff/api/tenants/demo/applications/app-1/fees");
  });

  it("useOverrideFee PATCHes amount+reason", async () => {
    const spy = mockFetch(jsonResponse({}));
    const { result } = renderHook(() => useOverrideFee("demo", "app-1"), { wrapper });
    result.current.mutate({ itemId: "i1", amount: 999, reason: "relationship" });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    const [url, init] = spy.mock.calls[0];
    expect(url).toBe("/bff/api/tenants/demo/applications/app-1/fees/i1");
    expect(init?.method).toBe("PATCH");
    expect(JSON.parse(init?.body as string)).toEqual({ amount: 999, reason: "relationship" });
  });

  it("useOverrideFee sends a null amount to clear", async () => {
    const spy = mockFetch(jsonResponse({}));
    const { result } = renderHook(() => useOverrideFee("demo", "app-1"), { wrapper });
    result.current.mutate({ itemId: "o1", amount: null, reason: "revert" });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(JSON.parse(spy.mock.calls[0][1]?.body as string)).toEqual({
      amount: null,
      reason: "revert",
    });
  });

  it("useLockRate POSTs eval+product+rate", async () => {
    const spy = mockFetch(jsonResponse({ lockId: "l1", expiresUtc: "2026-03-01T00:00:00Z" }));
    const { result } = renderHook(() => useLockRate("demo", "app-1"), { wrapper });
    result.current.mutate({ evalId: "ev1", productCode: "CNV30", rate: 6.5, lockTermDays: 30 });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(spy.mock.calls[0][0]).toBe("/bff/api/tenants/demo/applications/app-1/lock");
    expect(JSON.parse(spy.mock.calls[0][1]?.body as string)).toEqual({
      evalId: "ev1",
      productCode: "CNV30",
      rate: 6.5,
      lockTermDays: 30,
    });
  });

  it("useUnlockRate POSTs the reason", async () => {
    const spy = mockFetch(jsonResponse(true));
    const { result } = renderHook(() => useUnlockRate("demo", "app-1"), { wrapper });
    result.current.mutate("borrower renegotiated");
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(spy.mock.calls[0][0]).toBe("/bff/api/tenants/demo/applications/app-1/unlock");
    expect(JSON.parse(spy.mock.calls[0][1]?.body as string)).toEqual({
      reason: "borrower renegotiated",
    });
  });

  it("useRequestPackage POSTs kind+reason codes", async () => {
    const spy = mockFetch(jsonResponse({ docId: "d1", deduplicated: false }, 202));
    const { result } = renderHook(() => useRequestPackage("demo", "app-1"), { wrapper });
    result.current.mutate({ kind: "le-package", reasonCodes: ["changed-circumstance"] });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(spy.mock.calls[0][0]).toBe("/bff/api/tenants/demo/applications/app-1/packages");
    expect(JSON.parse(spy.mock.calls[0][1]?.body as string)).toEqual({
      kind: "le-package",
      reasonCodes: ["changed-circumstance"],
    });
  });
});

describe("admin hooks", () => {
  it("useTenantUsers/useTenantGroups map the lists", async () => {
    const spy = mockFetch();
    spy.mockResolvedValueOnce(
      jsonResponse({
        data: { tenantUsers: [{ id: "u1", email: "a@b.c", displayName: "Ada", status: "active", roles: ["LoanOfficer"] }] },
      }),
    ).mockResolvedValueOnce(
      jsonResponse({
        data: { tenantGroups: [{ id: "g1", name: "officers", roles: ["LoanOfficer"], memberCount: 2 }] },
      }),
    );
    const users = renderHook(() => useTenantUsers("demo"), { wrapper });
    const groups = renderHook(() => useTenantGroups("demo"), { wrapper });
    await waitFor(() => expect(users.result.current.isSuccess).toBe(true));
    await waitFor(() => expect(groups.result.current.isSuccess).toBe(true));
    expect(users.result.current.data?.[0].displayName).toBe("Ada");
    expect(groups.result.current.data?.[0].memberCount).toBe(2);
  });

  it("useGroupMembers returns the member id list", async () => {
    mockFetch(jsonResponse({ data: { groupMembers: ["u1", "u2"] } }));
    const { result } = renderHook(() => useGroupMembers("demo", "g1"), { wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(["u1", "u2"]);
  });

  it("useTenantAudit sends filters as GraphQL variables", async () => {
    const spy = mockFetch(jsonResponse({ data: { tenantAudit: [] } }));
    const { result } = renderHook(
      () => useTenantAudit("demo", { actor: "officer", action: "reveal", since: "2026-01-01" }),
      { wrapper },
    );
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    const body = JSON.parse(spy.mock.calls[0][1]?.body as string);
    expect(body.variables).toMatchObject({ actor: "officer", action: "reveal", since: "2026-01-01" });
  });

  it("useRetention maps the report", async () => {
    mockFetch(
      jsonResponse({
        data: {
          retention: {
            applications: [{ appId: "a1", createdUtc: "2026-01-01", retentionUntilUtc: "2028-02-01" }],
            earliestClearUtc: "2028-02-01T00:00:00Z",
          },
        },
      }),
    );
    const { result } = renderHook(() => useRetention("demo"), { wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.applications).toHaveLength(1);
    expect(result.current.data?.earliestClearUtc).toBe("2028-02-01T00:00:00Z");
  });

  it("useCreateInvite POSTs the email and returns the link", async () => {
    const spy = mockFetch(
      jsonResponse({ inviteId: "inv1", inviteUrl: "http://x/invite/abc", expiresUtc: "2026-02-01T00:00:00Z" }),
    );
    const { result } = renderHook(() => useCreateInvite("demo"), { wrapper });
    result.current.mutate("borrower@example.test");
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(spy.mock.calls[0][0]).toBe("/bff/api/admin/tenants/demo/invites");
    expect(result.current.data?.data.inviteUrl).toContain("invite");
  });

  it("group mutations hit the admin routes", async () => {
    const spy = mockFetch(jsonResponse({}));
    const create = renderHook(() => useCreateGroup("demo"), { wrapper });
    const update = renderHook(() => useUpdateGroup("demo"), { wrapper });
    const del = renderHook(() => useDeleteGroup("demo"), { wrapper });
    const add = renderHook(() => useAddMember("demo"), { wrapper });
    const rm = renderHook(() => useRemoveMember("demo"), { wrapper });

    create.result.current.mutate({ name: "ops", roles: ["LoanOfficer"] });
    await waitFor(() => expect(create.result.current.isSuccess).toBe(true));
    update.result.current.mutate({ gid: "g1", name: "ops2" });
    await waitFor(() => expect(update.result.current.isSuccess).toBe(true));
    add.result.current.mutate({ gid: "g1", userId: "u9" });
    await waitFor(() => expect(add.result.current.isSuccess).toBe(true));
    rm.result.current.mutate({ gid: "g1", userId: "u9" });
    await waitFor(() => expect(rm.result.current.isSuccess).toBe(true));
    del.result.current.mutate("g1");
    await waitFor(() => expect(del.result.current.isSuccess).toBe(true));

    const urls = spy.mock.calls.map((c) => `${c[1]?.method} ${c[0]}`);
    expect(urls).toEqual([
      "POST /bff/api/admin/tenants/demo/groups",
      "PUT /bff/api/admin/tenants/demo/groups/g1",
      "POST /bff/api/admin/tenants/demo/groups/g1/members",
      "DELETE /bff/api/admin/tenants/demo/groups/g1/members/u9",
      "DELETE /bff/api/admin/tenants/demo/groups/g1",
    ]);
  });
});
