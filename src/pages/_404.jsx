export function NotFound() {
  return (
    <section className="mt-10 flex min-h-[60vh] animate-fade-in flex-col items-center justify-center rounded-3xl bg-gradient-to-br from-primary/10 to-secondary/10 p-10 text-center shadow-2xl">
      <h1 className="mb-6 animate-fade-in-down bg-gradient-to-r from-primary to-secondary bg-clip-text font-extrabold text-6xl text-transparent">
        404: Not Found
      </h1>
      <p className="mb-10 animate-fade-in-up text-2xl text-base-content/80">
        It's gone :(
      </p>
      <a
        className="btn btn-primary btn-lg animate-fade-in-up rounded-full px-10"
        href="/"
      >
        Back to Home
      </a>
    </section>
  );
}
