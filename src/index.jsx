import {
  hydrate,
  LocationProvider,
  Route,
  Router,
  prerender as ssr,
} from 'preact-iso';
import { Footer } from './components/Footer.jsx';
import { Header } from './components/Header.jsx';
import { NotFound } from './pages/_404.jsx';
import { About } from './pages/About/index.jsx';
import { Contact } from './pages/Contact/index.jsx';
import { Home } from './pages/Home/index.jsx';
import { Pricing } from './pages/Pricing/index.jsx';
import './main.css';

export function App() {
  return (
    <LocationProvider>
      <Header />
      <main className="min-h-screen bg-gradient-to-b from-base-100 to-base-200">
        <Router>
          <Route component={Home} path="/" />
          <Route component={About} path="/about" />
          <Route component={Pricing} path="/pricing" />
          <Route component={Contact} path="/contact" />
          <Route component={NotFound} default />
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
