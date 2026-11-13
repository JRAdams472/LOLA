import "@testing-library/jest-dom";
import { render, screen } from "@testing-library/react";

import StatusChip from "@/components/StatusChip";
import ErrorPanel from "@/components/ErrorPanel";
import TridCountdown from "@/components/TridCountdown";

describe("StatusChip", () => {
  it("renders the status label", () => {
    render(<StatusChip status="Priced" />);
    expect(screen.getByText("Priced")).toBeInTheDocument();
  });

  it("renders unknown statuses without crashing", () => {
    render(<StatusChip status="Something New" />);
    expect(screen.getByText("Something New")).toBeInTheDocument();
  });
});

describe("ErrorPanel", () => {
  it("shows the error message verbatim", () => {
    render(<ErrorPanel error={new Error("validation failed: income required")} />);
    expect(
      screen.getByText("validation failed: income required"),
    ).toBeInTheDocument();
  });

  it("names the next action when the error is opaque", () => {
    render(<ErrorPanel error={null} />);
    expect(screen.getByText(/Refresh the page/)).toBeInTheDocument();
  });
});

describe("TridCountdown", () => {
  it("renders an em dash when the clock hasn't stamped", () => {
    render(<TridCountdown deadlineUtc={null} />);
    expect(screen.getByText("—")).toBeInTheDocument();
  });

  it("shows 'due today' on the deadline date", () => {
    const today = new Date().toISOString().slice(0, 10);
    render(<TridCountdown deadlineUtc={`${today}T00:00:00.000Z`} />);
    expect(screen.getByText("due today")).toBeInTheDocument();
  });

  it("shows overdue once the date has passed", () => {
    render(<TridCountdown deadlineUtc="2000-01-01T00:00:00.000Z" />);
    expect(screen.getByText(/overdue/)).toBeInTheDocument();
  });
});
