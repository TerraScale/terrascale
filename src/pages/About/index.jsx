export function About() {
  return (
    <div className="mx-auto mt-10 max-w-4xl animate-fade-in rounded-3xl bg-gradient-to-br from-primary/10 to-secondary/10 px-4 py-20 shadow-2xl">
      <section className="mb-16 animate-fade-in-down">
        <h1 className="mb-4 bg-gradient-to-r from-primary to-secondary bg-clip-text text-center font-extrabold text-5xl text-transparent">
          About TerraScale
        </h1>
        <p className="mb-8 text-center text-base-content/80 text-xl">
          Our mission is to empower businesses with scalable, secure, and
          innovative technology solutions. We believe in transparency,
          customer-centricity, and continuous improvement.
        </p>
      </section>
      <section className="animate-fade-in-right">
        <h2 className="mb-8 text-center font-semibold text-2xl">Our Team</h2>
        <div className="flex justify-center">
          <div className="card flex animate-fade-in-up flex-col items-center rounded-2xl border border-base-200 bg-base-100 p-8 shadow-xl">
            <figure className="px-10 pt-6">
              <img
                alt="Mario Gabriell Karaziaki Belchior"
                className="h-24 w-24 rounded-full border-4 border-primary object-cover shadow-md"
                src="https://avatars.githubusercontent.com/u/1010107?v=4"
              />
            </figure>
            <div className="card-body items-center text-center">
              <h3 className="card-title font-bold text-xl">
                Mario Gabriell Karaziaki Belchior
              </h3>
              <p className="font-medium">CEO & Founder</p>
              <p className="text-base text-base-content/60">
                Building the future of scalable technology with passion and
                vision.
              </p>
            </div>
          </div>
        </div>
      </section>
    </div>
  );
}
