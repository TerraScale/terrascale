import type { Metadata } from "next";

import { StudioNav } from "@/components/shell/StudioNav";

import "./globals.css";

export const metadata: Metadata = {
  title: "TerraBase Studio",
  description: "TerraBase operations studio",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  // Dark is the studio default (operations tool), mirroring the Dashboard
  // shell convention; theming toggles arrive with a later studio ticket.
  return (
    <html lang="en" className="dark" suppressHydrationWarning>
      <body className="min-h-svh bg-canvas text-ink">
        <a
          href="#studio-main"
          className="sr-only focus:not-sr-only focus:absolute focus:top-2 focus:left-2 focus:z-10 focus:rounded-panel focus:bg-surface focus:px-3 focus:py-2 focus:text-ink"
        >
          Skip to content
        </a>
        <div className="mx-auto flex min-h-svh max-w-5xl flex-col px-4 pb-10">
          <header className="sticky top-0 z-10 flex flex-wrap items-center justify-between gap-3 border-b border-line bg-canvas/90 py-3 backdrop-blur">
            <p className="flex items-baseline gap-2">
              <span className="text-base font-semibold text-ink">TerraBase Studio</span>
              <span className="text-xs text-ink-faint">TerraBase operations</span>
            </p>
            <StudioNav />
          </header>
          <main id="studio-main" className="flex-1 py-6">
            {children}
          </main>
          <footer className="border-t border-line pt-3 text-xs text-ink-faint">
            <p>
              Local operator tool — typed reads against the C# API host (dev fixture until SE-08
              lands). No authentication gate on this surface (charter PD-2026-08-26-11).
            </p>
          </footer>
        </div>
      </body>
    </html>
  );
}
