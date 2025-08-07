import { Link } from 'react-router-dom'
import { Home, Search, ArrowLeft } from 'lucide-react'

const NotFound = () => {
  return (
    <section className="section-padding bg-gradient-to-b from-gray-50 to-white min-h-screen flex items-center">
      <div className="max-w-7xl mx-auto container-padding">
        <div className="text-center max-w-2xl mx-auto">
          {/* Funny SVG Illustration */}
          <div className="mb-8">
            <svg
              className="w-64 h-64 mx-auto text-gray-300"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
              xmlns="http://www.w3.org/2000/svg"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={1.5}
                d="M9.172 16.172a4 4 0 015.656 0M9 12h6m-3-4v.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
              />
            </svg>
          </div>

          {/* 404 Number */}
          <div className="mb-6">
            <h1 className="text-8xl md:text-9xl font-bold gradient-text mb-4">
              404
            </h1>
            <h2 className="text-3xl md:text-4xl font-bold text-gray-900 mb-4">
              Not Found
            </h2>
          </div>

          {/* Message */}
          <p className="text-xl text-gray-600 mb-2">
            It's gone :(
          </p>
          <p className="text-lg text-gray-500 mb-8">
            The page you're looking for seems to have vanished into the digital void. 
            Don't worry, even the best explorers sometimes take a wrong turn!
          </p>

          {/* Action Buttons */}
          <div className="flex flex-col sm:flex-row gap-4 justify-center items-center">
            <Link
              to="/"
              className="btn-primary inline-flex items-center"
            >
              <Home className="mr-2 h-4 w-4" />
              Back to Home
            </Link>
            <Link
              to="/contact"
              className="btn-secondary inline-flex items-center"
            >
              <Search className="mr-2 h-4 w-4" />
              Contact Support
            </Link>
          </div>

          {/* Helpful Links */}
          <div className="mt-12 pt-8 border-t border-gray-200">
            <p className="text-gray-600 mb-4">Or try one of these popular pages:</p>
            <div className="flex flex-wrap justify-center gap-6 text-sm">
              <Link
                to="/about"
                className="text-primary-600 hover:text-primary-700 transition-colors duration-200"
              >
                About Us
              </Link>
              <Link
                to="/pricing"
                className="text-primary-600 hover:text-primary-700 transition-colors duration-200"
              >
                Pricing
              </Link>
              <Link
                to="/contact"
                className="text-primary-600 hover:text-primary-700 transition-colors duration-200"
              >
                Contact
              </Link>
            </div>
          </div>

          {/* Fun Animation */}
          <div className="mt-8">
            <div className="inline-flex items-center space-x-2 text-gray-400">
              <ArrowLeft className="h-4 w-4 animate-pulse" />
              <span className="text-sm">This way back to safety</span>
            </div>
          </div>
        </div>
      </div>
    </section>
  )
}

export default NotFound