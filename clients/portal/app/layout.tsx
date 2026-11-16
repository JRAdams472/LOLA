import type { Metadata } from "next";

import "./globals.css";

export const metadata: Metadata = {
  title: "LOLA Borrower Portal",
  description: "Complete your loan application, upload documents, and track your status",
};

export default function RootLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <html lang="en">
      <body className="min-h-screen bg-canvas text-text antialiased">
        {children}
      </body>
    </html>
  );
}
