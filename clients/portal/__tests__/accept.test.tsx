import "@testing-library/jest-dom";
import { render, screen, fireEvent } from "@testing-library/react";

import AcceptPage from "@/app/invites/accept/page";

// The form reads the invite token from the query string.
jest.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams("token=abc123"),
}));

describe("invite accept page", () => {
  it("keeps Create account disabled until the form is valid", () => {
    render(<AcceptPage />);
    const button = screen.getByRole("button", { name: "Create account" });
    expect(button).toBeDisabled();

    fireEvent.change(screen.getByLabelText(/Your name/), {
      target: { value: "Sam Rivera" },
    });
    fireEvent.change(screen.getByLabelText(/^Password/), {
      target: { value: "short" },
    });
    expect(button).toBeDisabled(); // < 12 chars

    fireEvent.change(screen.getByLabelText(/^Password/), {
      target: { value: "a-long-enough-password" },
    });
    fireEvent.change(screen.getByLabelText(/Confirm password/), {
      target: { value: "a-long-enough-password" },
    });
    expect(button).toBeEnabled();
  });

  it("flags mismatched passwords", () => {
    render(<AcceptPage />);
    fireEvent.change(screen.getByLabelText(/^Password/), {
      target: { value: "a-long-enough-password" },
    });
    fireEvent.change(screen.getByLabelText(/Confirm password/), {
      target: { value: "different-password" },
    });
    expect(screen.getByText("Passwords don’t match.")).toBeInTheDocument();
  });

  function fillValid() {
    fireEvent.change(screen.getByLabelText(/Your name/), {
      target: { value: "Sam Rivera" },
    });
    fireEvent.change(screen.getByLabelText(/^Password/), {
      target: { value: "a-long-enough-password" },
    });
    fireEvent.change(screen.getByLabelText(/Confirm password/), {
      target: { value: "a-long-enough-password" },
    });
  }

  it("posts the token and shows the sign-in handoff on success", async () => {
    global.fetch = jest.fn(() =>
      Promise.resolve({ ok: true, json: () => Promise.resolve({}) } as Response),
    );
    render(<AcceptPage />);
    fillValid();
    fireEvent.click(screen.getByRole("button", { name: "Create account" }));

    expect(global.fetch).toHaveBeenCalledWith("/api/invite-accept",
      expect.objectContaining({
        method: "POST",
        body: JSON.stringify({
          token: "abc123",
          displayName: "Sam Rivera",
          password: "a-long-enough-password",
        }),
      }));
    expect(
      await screen.findByText("Account created"),
    ).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Sign in" })).toHaveAttribute(
      "href", "/auth/login");
  });

  it("surfaces the server's problem detail on rejection", async () => {
    global.fetch = jest.fn(() =>
      Promise.resolve({
        ok: false,
        json: () => Promise.resolve({ detail: "Invite already used" }),
      } as Response),
    );
    render(<AcceptPage />);
    fillValid();
    fireEvent.click(screen.getByRole("button", { name: "Create account" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Invite already used");
  });

  it("shows a generic error when the request fails outright", async () => {
    global.fetch = jest.fn(() => Promise.reject(new Error("offline")));
    render(<AcceptPage />);
    fillValid();
    fireEvent.click(screen.getByRole("button", { name: "Create account" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      /try again/);
  });
});
