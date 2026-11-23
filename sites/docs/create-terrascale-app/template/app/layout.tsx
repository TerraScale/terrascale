import type { Metadata } from "next";

export const metadata: Metadata = {
  title: "TerraScale starter",
  description: "Next.js + TerraScale starter.",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
