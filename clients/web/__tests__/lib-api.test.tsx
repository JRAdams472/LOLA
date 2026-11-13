import "@testing-library/jest-dom";
import { renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";

import { apiGet, apiPost, gql, mutationError } from "@/lib/api";
import {
  useApplication,
  useApplicationAudit,
  useApplications,
  useCorrect,
  useCreateDraft,
  useLoanSearch,
  usePatchDraft,
  useReveal,
  useSteps,
  useSubmit,
} from "@/lib/loans";

const fetchMock = () => {
  const fn = jest.fn();
  (globalThis as { fetch?: unknown }).fetch = fn;
  return fn;
};
const mockFetch = (value?: Response) => {
  const fn = fetchMock();
  return value === undefined ? fn : fn.mockResolvedValue(value);
};
const mockFetchOnce = (...args: Parameters<jest.Mock["mockResolvedValue"]>) =>
  fetchMock().mockResolvedValueOnce(...args);

// jsdom has no fetch/Response — a response-shaped stub is enough
// for the BFF helpers under test (ok/status/json only).
const jsonResponse = (body: unknown, status = 200) =>
  ({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  }) as Response;

const wrapper = ({ children }: { children: ReactNode }) => (
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    {children}
  </QueryClientProvider>
);

describe("api helpers", () => {
  afterEach(() => jest.restoreAllMocks());

  it("apiGet returns the parsed body", async () => {
    mockFetch(jsonResponse({ ok: true }));
    await expect(apiGet<{ ok: boolean }>("/x")).resolves.toEqual({ ok: true });
  });

  it("apiGet throws on non-ok", async () => {
    mockFetch(jsonResponse({}, 500));
    await expect(apiGet("/x")).rejects.toThrow("500");
  });

  it("apiPost sends JSON and surfaces {status,data} â€” even on 422", async () => {
    const spy = mockFetch(
        jsonResponse({ errors: [{ path: "p", code: "c", message: "m" }] }, 422),
      );
    const res = await apiPost("/x", { patch: { a: "1" } });
    expect(spy).toHaveBeenCalledWith(
      "/bff/x",
      expect.objectContaining({ method: "POST" }),
    );
    expect(res.status).toBe(422);
    expect((res.data as { errors: unknown[] }).errors).toHaveLength(1);
  });

  it("gql returns data and throws on GraphQL errors", async () => {
    const spy = mockFetchOnce(jsonResponse({ data: { ping: "pong" } }));
    await expect(gql<{ ping: string }>("{ ping }", { a: 1 })).resolves.toEqual({
      ping: "pong",
    });
    expect(spy).toHaveBeenCalledWith(
      "/bff/graphql",
      expect.objectContaining({ method: "POST" }),
    );

    mockFetchOnce(
        jsonResponse({ errors: [{ message: "bad field" }] }),
      );
    await expect(gql("{ bad }")).rejects.toThrow("bad field");
  });


});

describe("mutationError", () => {
  it("extracts detail/title/error from a failed apiWrite result", () => {
    expect(
      mutationError({
        data: { status: 403, data: { detail: "staff only" } },
        isError: false,
        error: null,
      }),
    ).toBe("staff only");
    expect(
      mutationError({
        data: { status: 500, data: { title: "oops" } },
        isError: false,
        error: null,
      }),
    ).toBe("oops");
  });

  it("falls back to the thrown error and null when clean", () => {
    expect(
      mutationError({ isError: true, error: new Error("net down") }),
    ).toContain("net down");
    expect(
      mutationError({ data: { status: 200, data: {} }, isError: false, error: null }),
    ).toBeNull();
  });
});

describe("loan hooks", () => {
  afterEach(() => jest.restoreAllMocks());

  it("useApplications maps the GraphQL list", async () => {
    mockFetch(
      jsonResponse({
        data: { applications: [{ id: "app-1", status: "draft" }] },
      }),
    );
    const { result } = renderHook(() => useApplications("demo"), { wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toHaveLength(1);
    expect(result.current.data![0].id).toBe("app-1");
  });

  it("useApplication returns the workspace view", async () => {
    mockFetch(
      jsonResponse({
        data: {
          application: { id: "app-1", status: "draft", docJson: "{}" },
        },
      }),
    );
    const { result } = renderHook(() => useApplication("demo", "app-1"), {
      wrapper,
    });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.docJson).toBe("{}");
  });

  it("useSteps and useApplicationAudit return event lists", async () => {
    const spy = mockFetch();
    spy.mockResolvedValueOnce(
      jsonResponse({
        data: { steps: [{ id: "s1", step: "intake", event: "enter" }] },
      }),
    ).mockResolvedValueOnce(
      jsonResponse({
        data: { applicationAudit: [{ seq: 1, action: "write" }] },
      }),
    );
    const steps = renderHook(() => useSteps("demo", "app-1"), { wrapper });
    const audit = renderHook(
      () => useApplicationAudit("demo", "app-1", true),
      { wrapper },
    );
    await waitFor(() => expect(steps.result.current.isSuccess).toBe(true));
    await waitFor(() => expect(audit.result.current.isSuccess).toBe(true));
    expect(steps.result.current.data![0].step).toBe("intake");
    expect(audit.result.current.data![0].action).toBe("write");
  });

  it("useLoanSearch posts the term inside the GraphQL body", async () => {
    const spy = mockFetch(
      jsonResponse({
        data: { search: { applicationIds: ["app-1"] } },
      }),
    );
    const { result } = renderHook(() => useLoanSearch("demo"), { wrapper });
    result.current.mutate({ field: "ssn", value: "123456789" });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    const body = JSON.parse((spy.mock.calls[0][1]?.body as string) ?? "{}");
    expect(body.variables).toMatchObject({
      field: "ssn",
      value: "123456789",
    });
    expect(result.current.data).toEqual(["app-1"]);
  });

  it("usePatchDraft PATCHes the merge patch", async () => {
    const spy = mockFetch(jsonResponse({ ok: true, version: 2, errors: [] }));
    const { result } = renderHook(() => usePatchDraft("demo", "app-1"), {
      wrapper,
    });
    result.current.mutate({ "party[0].first_name": "Ada" });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    const [url, init] = spy.mock.calls[0];
    expect(url).toBe("/bff/api/tenants/demo/applications/app-1");
    expect(init?.method).toBe("PATCH");
    expect(JSON.parse(init?.body as string)).toEqual({
      patch: { "party[0].first_name": "Ada" },
    });
  });

  it("useSubmit POSTs the submit endpoint", async () => {
    const spy = mockFetch(jsonResponse({ ok: true, version: 1, errors: [] }));
    const { result } = renderHook(() => useSubmit("demo", "app-1"), { wrapper });
    result.current.mutate();
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(spy.mock.calls[0][0]).toBe(
      "/bff/api/tenants/demo/applications/app-1/submit",
    );
  });

  it("useCorrect POSTs the correction patch", async () => {
    const spy = mockFetch(jsonResponse({ ok: true, version: 3, errors: [] }));
    const { result } = renderHook(() => useCorrect("demo", "app-1"), {
      wrapper,
    });
    result.current.mutate({ loan_amount: "500000" });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(spy.mock.calls[0][0]).toBe(
      "/bff/api/tenants/demo/applications/app-1/correct",
    );
    expect(JSON.parse(spy.mock.calls[0][1]?.body as string)).toEqual({
      patch: { loan_amount: "500000" },
    });
  });

  it("useReveal POSTs the required reason", async () => {
    const spy = mockFetch(jsonResponse({ id: "app-1", doc: {} }));
    const { result } = renderHook(() => useReveal("demo", "app-1"), { wrapper });
    result.current.mutate("borrower verification");
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(spy.mock.calls[0][0]).toBe(
      "/bff/api/tenants/demo/applications/app-1/reveal",
    );
    expect(JSON.parse(spy.mock.calls[0][1]?.body as string)).toEqual({
      reason: "borrower verification",
    });
  });

  it("useCreateDraft POSTs an empty application", async () => {
    const spy = mockFetch(jsonResponse({ id: "app-new" }));
    const { result } = renderHook(() => useCreateDraft("demo"), { wrapper });
    result.current.mutate();
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(spy.mock.calls[0][0]).toBe("/bff/api/tenants/demo/applications");
    expect(result.current.data?.data.id).toBe("app-new");
  });
});
