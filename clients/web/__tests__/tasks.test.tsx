import "@testing-library/jest-dom";
import { renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";

import {
  TASK_KIND_LABEL,
  useClaimTask,
  useCompleteTask,
  useNextTask,
  useTasks,
  type QueueTask,
} from "@/lib/tasks";

const mockFetch = (value?: Response) => {
  const fn = jest.fn();
  (globalThis as { fetch?: unknown }).fetch = fn;
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

const task: QueueTask = {
  seq: 1,
  taskId: "t-1",
  tenantId: "ten",
  applicationId: "app-1",
  kind: "condition_blocking",
  title: "Review open condition",
  priority: 80,
  skillTags: ["underwriting"],
  dueUtc: null,
  state: "open",
  claimedBy: null,
  claimedUtc: null,
  completedUtc: null,
  createdUtc: "2026-01-01T00:00:00Z",
};

describe("task queue hooks", () => {
  it("useTasks queries GraphQL with skill filter", async () => {
    const fetch = mockFetch(
      jsonResponse({ data: { tasks: [task] } }),
    );
    const { result } = renderHook(
      () => useTasks("ten", ["underwriting"]),
      { wrapper },
    );
    await waitFor(() => expect(result.current.data).toHaveLength(1));
    const [url, init] = fetch.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/bff/graphql");
    const body = JSON.parse(init.body as string);
    expect(body.variables).toEqual({ t: "ten", s: ["underwriting"] });
    expect(body.query).toContain("tasks(tenantId: $t, skills: $s)");
  });

  it("useTasks is disabled without a tenant", () => {
    const fetch = mockFetch();
    renderHook(() => useTasks(null), { wrapper });
    expect(fetch).not.toHaveBeenCalled();
  });

  it("useNextTask returns the single highest-priority task", async () => {
    mockFetch(jsonResponse({ data: { nextTask: task } }));
    const { result } = renderHook(() => useNextTask("ten"), { wrapper });
    await waitFor(() =>
      expect(result.current.data?.taskId).toBe("t-1"),
    );
  });

  it("useNextTask tolerates an empty queue", async () => {
    mockFetch(jsonResponse({ data: { nextTask: null } }));
    const { result } = renderHook(() => useNextTask("ten"), { wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toBeNull();
  });

  it("claim posts to the REST transition endpoint", async () => {
    const fetch = mockFetch(
      jsonResponse({ ...task, state: "claimed", claimedBy: "me" }),
    );
    const { result } = renderHook(() => useClaimTask("ten"), { wrapper });
    await result.current.mutateAsync("t-1");
    const [url, init] = fetch.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/bff/api/tenants/ten/tasks/t-1/claim");
    expect(init.method).toBe("POST");
  });

  it("complete posts to the REST transition endpoint", async () => {
    const fetch = mockFetch(
      jsonResponse({ ...task, state: "done" }),
    );
    const { result } = renderHook(() => useCompleteTask("ten"), { wrapper });
    await result.current.mutateAsync("t-1");
    const [url] = fetch.mock.calls[0] as [string];
    expect(url).toBe("/bff/api/tenants/ten/tasks/t-1/complete");
  });
});

describe("task kind labels", () => {
  it("covers the evaluator kinds", () => {
    expect(TASK_KIND_LABEL.condition_blocking).toBeTruthy();
    expect(TASK_KIND_LABEL.step_review).toBeTruthy();
    expect(TASK_KIND_LABEL.trid_deadline).toBeTruthy();
    expect(TASK_KIND_LABEL.stale_draft).toBeTruthy();
  });
});
