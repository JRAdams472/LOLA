import "@testing-library/jest-dom";
import { render, screen, fireEvent } from "@testing-library/react";

import MaskedValue from "@/components/MaskedValue";
import FieldInput from "@/components/FieldInput";
import { getPath, setPath, isMasked, type Doc } from "@/lib/doc";
import { fieldErrors } from "@/lib/loans";
import type { FieldSpec } from "@/lib/field-map";

describe("doc paths", () => {
  const doc: Doc = {
    party: [
      {
        first_name: "Ada",
        address: [{ street: "12 Analytical Way", city: "Springfield" }],
      },
    ],
    subject_property: { street: "1 Prime Plaza" },
  };

  it("gets nested and bracketed paths", () => {
    expect(getPath(doc, "party[0].first_name")).toBe("Ada");
    expect(getPath(doc, "party[0].address[0].street")).toBe("12 Analytical Way");
    expect(getPath(doc, "subject_property.street")).toBe("1 Prime Plaza");
  });

  it("returns undefined for missing paths", () => {
    expect(getPath(doc, "party[1].first_name")).toBeUndefined();
    expect(getPath(doc, "party[0].address[0].zip")).toBeUndefined();
    expect(getPath(null, "party[0].ssn")).toBeUndefined();
  });

  it("sets paths, creating intermediate objects/arrays", () => {
    const d: Doc = {};
    setPath(d, "party[0].address[0].street", "9 Loop Lane");
    expect(getPath(d, "party[0].address[0].street")).toBe("9 Loop Lane");
    setPath(d, "party[0].first_name", "Ada");
    expect(getPath(d, "party[0].first_name")).toBe("Ada");
  });
});

describe("isMasked", () => {
  it("detects the •••+last-4 mask shape", () => {
    expect(isMasked("•••6789")).toBe(true);
    expect(isMasked("•••")).toBe(true);
    expect(isMasked("Ada")).toBe(false);
    expect(isMasked(undefined)).toBe(false);
  });
});

describe("MaskedValue", () => {
  it("renders plaintext values as-is", () => {
    render(<MaskedValue value="Ada" />);
    expect(screen.getByText("Ada")).toBeInTheDocument();
    expect(screen.queryByLabelText("reveal")).not.toBeInTheDocument();
  });

  it("renders the mask with a staff-only reveal affordance", () => {
    render(<MaskedValue value="•••6789" canReveal onReveal={() => {}} />);
    expect(screen.getByText("•••6789")).toBeInTheDocument();
    expect(screen.getByLabelText("reveal")).toBeInTheDocument();
  });

  it("hides the reveal affordance for non-staff", () => {
    render(<MaskedValue value="•••6789" canReveal={false} />);
    expect(screen.queryByLabelText("reveal")).not.toBeInTheDocument();
  });
});

const ssnSpec: FieldSpec = {
  path: "party[0].ssn",
  label: "SSN",
  type: "string",
  enum: null,
  piiClass: "Ssn",
  required: true,
  section: "borrower",
};

const purposeSpec: FieldSpec = {
  path: "loan_purpose",
  label: "Loan purpose",
  type: "enum",
  enum: ["purchase", "refinance"],
  piiClass: null,
  required: true,
  section: "loan",
};

describe("FieldInput", () => {
  it("renders an editable input for visible values", () => {
    render(
      <FieldInput spec={ssnSpec} value="123456789" editable onChange={() => {}} />,
    );
    expect(screen.getByDisplayValue("123456789")).toBeInTheDocument();
  });

  it("renders the mask (not an input) for masked values", () => {
    render(
      <FieldInput spec={ssnSpec} value="•••6789" editable canReveal onReveal={() => {}} onChange={() => {}} />,
    );
    expect(screen.getByText("•••6789")).toBeInTheDocument();
    expect(screen.queryByDisplayValue("•••6789")).not.toBeInTheDocument();
  });

  it("swaps in an empty input via the replace affordance", () => {
    render(
      <FieldInput spec={ssnSpec} value="•••6789" editable onChange={() => {}} />,
    );
    fireEvent.click(screen.getByLabelText("replace"));
    expect(screen.getByPlaceholderText("Enter new value")).toBeInTheDocument();
  });

  it("shows the 422 error under the field", () => {
    render(
      <FieldInput spec={ssnSpec} value="" editable error="required" onChange={() => {}} />,
    );
    expect(screen.getByText("required")).toBeInTheDocument();
  });

  it("renders enum options as a select", () => {
    render(
      <FieldInput spec={purposeSpec} value="purchase" editable onChange={() => {}} />,
    );
    expect(screen.getByText("Purchase")).toBeInTheDocument();
  });
});

describe("fieldErrors", () => {
  it("maps 422 problem-details extensions onto catalog paths", () => {
    const problem = {
      type: "https://httpstatuses.com/422",
      status: 422,
      errors: [
        { path: "party[0].ssn", code: "required", message: "ssn is required" },
        { path: "loan_amount", code: "required", message: "loan amount is required" },
      ],
    };
    const errors = fieldErrors(problem);
    expect(errors).toHaveLength(2);
    expect(errors[0].path).toBe("party[0].ssn");
    expect(errors[0].message).toBe("ssn is required");
  });

  it("returns [] for non-422 payloads", () => {
    expect(fieldErrors({ title: "bad request" })).toEqual([]);
    expect(fieldErrors(null)).toEqual([]);
    expect(fieldErrors(undefined)).toEqual([]);
  });
});
