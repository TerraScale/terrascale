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

  const isActivePath = (path: string) => {
    if (path === '/') {
      return location.pathname === '/'
    }
    return location.pathname.startsWith(path)
  }

  return (
    <header className="border-b border-[#1A1A24] sticky top-0 z-50 ts-glass-frosted bg-[#050508]/80 bg-grid-pattern">
      <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
        <div className="flex justify-between items-center py-3 relative">
          <Link to="/" className="flex items-center space-x-2 flex-shrink-0">
            <div className="w-8 h-8 bg-[#2563EB] ts-rounded-sm flex items-center justify-center shadow-offset-blue ts-hover-glow">
              <span className="text-white font-bold text-sm">T</span>
            </div>
            <span className="text-xl font-bold text-[#F8FAFC]">TerraScale</span>
          </Link>

          <nav className="hidden md:flex items-center space-x-1 absolute left-1/2 transform -translate-x-1/2">
            {navigation.map((item) => (
              <Link
                key={item.name}
                to={item.href}
                className={cn(
                  'px-3 py-2 ts-rounded-sm text-sm font-medium transition-all duration-200 flex items-center space-x-2 relative hover-lift',
                  isActivePath(item.href)
                    ? 'text-[#2563EB] bg-[#2563EB]/10 border border-[#2563EB]/30'
                    : 'text-[#94A3B8] hover:text-[#F8FAFC] hover:bg-[#0C0C10]'
                )}
              >
                <item.icon className="h-4 w-4" />
                <span>{item.name}</span>
              </Link>
            ))}
          </nav>

          <div className="hidden md:flex items-center space-x-3">
            <a
              href="https://docs.terrascale.tech"
              target="_blank"
              rel="noopener noreferrer"
              className="px-3 py-2 text-sm font-medium text-[#94A3B8] hover:text-[#F8FAFC] transition-colors duration-200 flex items-center space-x-2 hover-lift"
            >
              <BookOpen className="h-4 w-4" />
              <span>Docs</span>
            </a>
            <a
              href="https://status.terrascale.tech"
              target="_blank"
              rel="noopener noreferrer"
              className="px-3 py-2 text-sm font-medium text-[#94A3B8] hover:text-[#F8FAFC] transition-colors duration-200 flex items-center space-x-2 hover-lift"
            >
              <div className="flex items-center space-x-1.5">
                <div className="w-2 h-2 bg-[#10B981] ts-rounded-sm animate-pulse ts-glow-secondary"></div>
                <Activity className="h-4 w-4" />
              </div>
              <span>Status</span>
            </a>
            <a
              href="https://dash.terrascale.tech"
              target="_blank"
              rel="noopener noreferrer"
              className="ts-btn-primary px-4 py-2 ts-rounded-sm text-sm font-medium flex items-center space-x-2 shadow-offset-blue hover-lift hover-lift-active"
            >
              <LayoutDashboard className="h-4 w-4" />
              <span>Dashboard</span>
            </a>
          </div>

          <button
            type="button"
            className="md:hidden p-2 text-[#94A3B8] hover:text-[#F8FAFC] hover:bg-[#0C0C10] ts-rounded-sm border border-[#1A1A24]"
            onClick={() => setIsMenuOpen(!isMenuOpen)}
          >
            {isMenuOpen ? (
              <X className="h-5 w-5" />
            ) : (
              <Menu className="h-5 w-5" />
            )}
          </button>
        </div>

        {isMenuOpen && (
          <div className="md:hidden border-t border-[#1A1A24] py-4 bg-[#050508]">
            <nav className="flex flex-col space-y-2">
              {navigation.map((item) => (
                <Link
                  key={item.name}
                  to={item.href}
                  className={cn(
                    'flex items-center space-x-3 px-3 py-2 ts-rounded-sm text-sm font-medium transition-all duration-200',
                    isActivePath(item.href)
                      ? 'text-[#2563EB] bg-[#2563EB]/10 border border-[#2563EB]/30'
                      : 'text-[#94A3B8] hover:text-[#F8FAFC] hover:bg-[#0C0C10]'
                  )}
                  onClick={() => setIsMenuOpen(false)}
                >
                  <item.icon className="h-4 w-4" />
                  <span>{item.name}</span>
                </Link>
              ))}
              <div className="pt-4 mt-2 border-t border-[#1A1A24] space-y-2">
                <a
                  href="https://docs.terrascale.tech"
                  target="_blank"
                  rel="noopener noreferrer"
                  className="flex items-center space-x-2 px-3 py-2 text-sm font-medium text-[#94A3B8] hover:text-[#F8FAFC] hover:bg-[#0C0C10] ts-rounded-sm transition-all duration-200"
                  onClick={() => setIsMenuOpen(false)}
                >
                  <BookOpen className="h-4 w-4" />
                  <span>Documentation</span>
                </a>
                <a
                  href="https://status.terrascale.tech"
                  target="_blank"
                  rel="noopener noreferrer"
                  className="flex items-center space-x-2 px-3 py-2 text-sm font-medium text-[#94A3B8] hover:text-[#F8FAFC] hover:bg-[#0C0C10] ts-rounded-sm transition-all duration-200"
                  onClick={() => setIsMenuOpen(false)}
                >
                  <div className="flex items-center space-x-1.5">
                    <div className="w-2 h-2 bg-[#10B981] ts-rounded-sm animate-pulse ts-glow-secondary"></div>
                    <Activity className="h-4 w-4" />
                  </div>
                  <span>Status</span>
                </a>
                <a
                  href="https://dash.terrascale.tech"
                  target="_blank"
                  rel="noopener noreferrer"
                  className="ts-btn-primary px-3 py-2 ts-rounded-sm text-sm font-medium flex items-center justify-center w-full space-x-2 shadow-offset-blue"
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
