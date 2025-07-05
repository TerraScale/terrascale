export function NotFound() {
	return (
		<section className="min-h-[60vh] flex flex-col items-center justify-center text-center bg-gradient-to-br from-primary/10 to-secondary/10 rounded-3xl shadow-2xl mt-10 p-10 animate-fade-in">
			<h1 className="text-6xl font-extrabold mb-6 bg-gradient-to-r from-primary to-secondary bg-clip-text text-transparent animate-fade-in-down">404: Not Found</h1>
			<p className="text-2xl text-base-content/80 mb-10 animate-fade-in-up">It's gone :(</p>
			<a href="/" className="btn btn-primary btn-lg rounded-full px-10 animate-fade-in-up">Back to Home</a>
		</section>
	);
}
