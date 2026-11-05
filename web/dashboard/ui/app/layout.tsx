import type { Metadata } from "next";
import { Inter, JetBrains_Mono } from "next/font/google";
import "@/styles/tokens.css";
import "@/styles/app.css";
import { PrefsProvider, THEME_BOOT_SCRIPT } from "@/lib/prefs";
import { SessionProvider } from "@/lib/session";

// Fonts are downloaded at build time and served from our own site (HANDOFF §6.6), never from Google at runtime.
const inter = Inter({ subsets: ["latin"], variable: "--font-inter", display: "swap" });
const mono = JetBrains_Mono({ subsets: ["latin"], variable: "--font-jetbrains", display: "swap" });

export const metadata: Metadata = {
  title: "Trader Dashboard",
  description: "Private dashboard for the Trader bots",
  robots: { index: false, follow: false },
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" className={`${inter.variable} ${mono.variable}`} suppressHydrationWarning>
      <head>
        <script dangerouslySetInnerHTML={{ __html: THEME_BOOT_SCRIPT }} />
      </head>
      <body>
        <PrefsProvider>
          <SessionProvider>{children}</SessionProvider>
        </PrefsProvider>
      </body>
    </html>
  );
}
