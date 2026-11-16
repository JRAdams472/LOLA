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
});
