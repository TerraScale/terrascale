import { useState } from 'react'
import { Link, useLocation } from 'react-router-dom'
import { Menu, X, Home, Users, DollarSign, MessageCircle, BookOpen, Activity, LayoutDashboard } from 'lucide-react'
import { cn } from '../lib/utils'

const Header = () => {
  const [isMenuOpen, setIsMenuOpen] = useState(false)
  const location = useLocation()

  const navigation = [
    { name: 'Home', href: '/', icon: Home },
    { name: 'Pricing', href: '/pricing', icon: DollarSign },
    { name: 'Contact', href: '/contact', icon: MessageCircle },
    { name: 'About', href: '/about', icon: Users },
  ]

  const isActivePath = (path) => {
    if (path === '/') {
      return location.pathname === '/'
    }
    return location.pathname.startsWith(path)
  }

  return (
    <header className="bg-card border-b border-border sticky top-0 z-50 backdrop-blur-sm bg-card/80">
      <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
        <div className="flex justify-between items-center py-3 relative">
          {/* Logo */}
          <Link to="/" className="flex items-center space-x-2 flex-shrink-0">
            <div className="w-8 h-8 bg-gradient-to-r from-[#4379F2] to-[#6EC207] rounded-lg flex items-center justify-center">
              <span className="text-white font-bold text-sm">T</span>
            </div>
            <span className="text-xl font-bold text-foreground">TerraScale</span>
          </Link>

          {/* Desktop Navigation - Centered */}
          <nav className="hidden md:flex items-center space-x-1 absolute left-1/2 transform -translate-x-1/2">
            {navigation.map((item) => (
              item.external ? (
                <a
                  key={item.name}
                  href={item.href}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="px-3 py-2 rounded-lg text-sm font-medium transition-all duration-200 flex items-center space-x-2 text-muted-foreground hover:text-foreground hover:bg-accent"
                >
                  <item.icon className="h-4 w-4" />
                  <span>{item.name}</span>
                </a>
              ) : (
                <Link
                  key={item.name}
                  to={item.href}
                  className={cn(
                    'px-3 py-2 rounded-lg text-sm font-medium transition-all duration-200 flex items-center space-x-2 relative',
                    isActivePath(item.href)
                      ? 'text-primary bg-primary/10'
                      : 'text-muted-foreground hover:text-foreground hover:bg-accent'
                  )}
                >
                  <item.icon className="h-4 w-4" />
                  <span className={cn(
                    'relative',
                    isActivePath(item.href) && 'after:absolute after:bottom-0 after:left-0 after:right-0 after:h-0.5 after:bg-primary after:rounded-full'
                  )}>
                    {item.name}
                  </span>
                </Link>
              )
            ))}
          </nav>

          {/* Desktop CTA */}
          <div className="hidden md:flex items-center space-x-3">
            <a
              href="https://docs.terrascale.tech"
              target="_blank"
              rel="noopener noreferrer"
              className="px-3 py-2 text-sm font-medium text-muted-foreground hover:text-foreground transition-colors duration-200 flex items-center space-x-2"
            >
              <BookOpen className="h-4 w-4" />
              <span>Docs</span>
            </a>
            <a
              href="https://status.terrascale.tech"
              target="_blank"
              rel="noopener noreferrer"
              className="px-3 py-2 text-sm font-medium text-muted-foreground hover:text-foreground transition-colors duration-200 flex items-center space-x-2"
            >
              <div className="flex items-center space-x-1.5">
                <div className="w-2 h-2 bg-green-500 rounded-full animate-pulse"></div>
                <Activity className="h-4 w-4" />
              </div>
              <span>Status</span>
            </a>
            <a
              href="https://dash.terrascale.tech"
              target="_blank"
              rel="noopener noreferrer"
              className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] hover:from-[#3366E6] hover:to-[#5CAE06] text-white px-4 py-2 rounded-lg text-sm font-medium transition-all duration-200 flex items-center space-x-2"
            >
              <LayoutDashboard className="h-4 w-4" />
              <span>Dashboard</span>
            </a>
          </div>

          {/* Mobile menu button */}
          <button
            type="button"
            className="md:hidden p-2 text-muted-foreground hover:text-foreground hover:bg-accent rounded-lg"
            onClick={() => setIsMenuOpen(!isMenuOpen)}
          >
            {isMenuOpen ? (
              <X className="h-5 w-5" />
            ) : (
              <Menu className="h-5 w-5" />
            )}
          </button>
        </div>

        {/* Mobile Navigation */}
        {isMenuOpen && (
          <div className="md:hidden border-t border-border py-4">
            <nav className="flex flex-col space-y-2">
              {navigation.map((item) => (
                item.external ? (
                  <a
                    key={item.name}
                    href={item.href}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="flex items-center space-x-3 px-3 py-2 rounded-lg text-sm font-medium transition-all duration-200 text-muted-foreground hover:text-foreground hover:bg-accent"
                    onClick={() => setIsMenuOpen(false)}
                  >
                    <item.icon className="h-4 w-4" />
                    <span>{item.name}</span>
                  </a>
                ) : (
                  <Link
                    key={item.name}
                    to={item.href}
                    className={cn(
                      'flex items-center space-x-3 px-3 py-2 rounded-lg text-sm font-medium transition-all duration-200',
                      isActivePath(item.href)
                        ? 'text-primary bg-primary/10'
                        : 'text-muted-foreground hover:text-foreground hover:bg-accent'
                    )}
                    onClick={() => setIsMenuOpen(false)}
                  >
                    <item.icon className="h-4 w-4" />
                    <span className={cn(
                      'relative',
                      isActivePath(item.href) && 'after:absolute after:bottom-0 after:left-0 after:right-0 after:h-0.5 after:bg-primary after:rounded-full'
                    )}>
                      {item.name}
                    </span>
                  </Link>
                )
              ))}
              <div className="pt-4 mt-2 border-t border-border space-y-2">
                <a
                  href="https://docs.terrascale.tech"
                  target="_blank"
                  rel="noopener noreferrer"
                  className="flex items-center space-x-2 px-3 py-2 text-sm font-medium text-muted-foreground hover:text-foreground hover:bg-accent rounded-lg transition-all duration-200"
                  onClick={() => setIsMenuOpen(false)}
                >
                  <BookOpen className="h-4 w-4" />
                  <span>Documentation</span>
                </a>
                <a
                  href="https://status.terrascale.tech"
                  target="_blank"
                  rel="noopener noreferrer"
                  className="flex items-center space-x-2 px-3 py-2 text-sm font-medium text-muted-foreground hover:text-foreground hover:bg-accent rounded-lg transition-all duration-200"
                  onClick={() => setIsMenuOpen(false)}
                >
                  <div className="flex items-center space-x-1.5">
                    <div className="w-2 h-2 bg-green-500 rounded-full animate-pulse"></div>
                    <Activity className="h-4 w-4" />
                  </div>
                  <span>Status</span>
                </a>
                <a
                  href="https://dash.terrascale.tech"
                  target="_blank"
                  rel="noopener noreferrer"
                  className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] text-white px-3 py-2 rounded-lg text-sm font-medium transition-all duration-200 flex items-center justify-center w-full space-x-2"
                  onClick={() => setIsMenuOpen(false)}
                >
                  <LayoutDashboard className="h-4 w-4" />
                  <span>Dashboard</span>
                </a>
              </div>
            </nav>
          </div>
        )}
      </div>
    </header>
  )
}

export default Header