import type { Metadata } from "next";

import AppShell from "../components/AppShell";
import Providers from "./providers";

export const metadata: Metadata = {
  title: "LOLA Officer Console",
  description: "Loan origination — pipeline, workspace, closing costs",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <body>
        <Providers>
          <AppShell>{children}</AppShell>
        </Providers>
      </body>
    </html>
  );
}
