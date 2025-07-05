import { LocationProvider, Router, Route, hydrate, prerender as ssr } from 'preact-iso';

import { Header } from './components/Header.jsx';
import { Footer } from './components/Footer.jsx';
import { Home } from './pages/Home/index.jsx';
import { About } from './pages/About/index.jsx';
import { Pricing } from './pages/Pricing/index.jsx';
import { Contact } from './pages/Contact/index.jsx';
import { NotFound } from './pages/_404.jsx';
import './main.css';

export function App() {
	return (
		<LocationProvider>
			<Header />
			<main className="min-h-screen bg-gradient-to-b from-base-100 to-base-200">
				<Router>
					<Route path="/" component={Home} />
					<Route path="/about" component={About} />
					<Route path="/pricing" component={Pricing} />
					<Route path="/contact" component={Contact} />
					<Route default component={NotFound} />
				</Router>
			</main>
			<Footer />
		</LocationProvider>
	);
}

if (typeof window !== 'undefined') {
	hydrate(<App />, document.getElementById('app'));
}

export async function prerender(data) {
	return await ssr(<App {...data} />);
}
