import type { NextConfig } from "next";

// Static export: the screens are plain files on Hostinger; all data comes from the PHP API after sign-in (dashboard.md §4.1).
const nextConfig: NextConfig = {
  output: "export",
  trailingSlash: true, // /scout/ → scout/index.html, which Apache serves as is
  images: { unoptimized: true },
  poweredByHeader: false,
};

export default nextConfig;
