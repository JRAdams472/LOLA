import "@testing-library/jest-dom";
import { render, screen } from "@testing-library/react";

import StatusChip from "@/components/StatusChip";
import ErrorPanel from "@/components/ErrorPanel";

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
