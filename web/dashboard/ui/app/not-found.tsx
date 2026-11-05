import Link from "next/link";

export default function NotFound() {
  return (
    <main className="center-page">
      <div style={{ textAlign: "center" }}>
        <h1>Page not found</h1>
        <p className="upd"><Link href="/scout/">Go to Scout</Link></p>
      </div>
    </main>
  );
}
