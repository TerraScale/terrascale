import { Link } from 'react-router-dom'
import { Twitter, Linkedin, Github, Mail, MessageSquare, ShieldCheck, Zap } from 'lucide-react'

const Footer = () => {
  const socialLinks = [
    { name: 'Discord', href: 'https://discord.gg/terrascale', icon: MessageSquare },
    { name: 'Twitter', href: 'https://twitter.com/terrascale', icon: Twitter },
    { name: 'LinkedIn', href: 'https://linkedin.com/company/terrascale', icon: Linkedin },
    { name: 'GitHub', href: 'https://github.com/terrascale', icon: Github },
  ]

  const footerLinks = {
    Product: [
      { name: 'Features', href: '/#features' },
      { name: 'Pricing', href: '/pricing' },
      { name: 'Documentation', href: 'https://docs.terrascale.tech' },
      { name: 'API Reference', href: 'https://docs.terrascale.tech/api' },
    ],
    Company: [
      { name: 'About', href: '/about' },
      { name: 'Contact', href: '/contact' },
      { name: 'Status', href: 'https://status.terrascale.tech' },
      { name: 'Blog', href: 'https://blog.terrascale.tech' },
    ],
    Resources: [
      { name: 'Help Center', href: 'https://help.terrascale.tech' },
      { name: 'Community', href: 'https://discord.gg/terrascale' },
      { name: 'Security', href: '/security' },
      { name: 'Terms', href: '/terms' },
    ],
  }

  return (
    <footer className="border-t border-[#1A1A24] bg-[#050508] bg-grid-pattern">
      <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
        <div className="py-8">
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-5 gap-8">
            <div className="lg:col-span-2">
              <Link to="/" className="flex items-center space-x-2 mb-4">
                <div className="w-8 h-8 bg-[#2563EB] ts-rounded-sm flex items-center justify-center shadow-offset-blue ts-hover-glow">
                  <span className="text-white font-bold text-sm">T</span>
                </div>
                <span className="text-xl font-bold text-[#F8FAFC]">TerraScale</span>
              </Link>
              <p className="text-[#94A3B8] mb-4 max-w-sm text-sm">
                Empowering businesses with secure, scalable, and innovative cloud solutions.
                Scale your tech, effortlessly.
              </p>

              <div className="space-y-2 text-sm text-[#94A3B8] mb-4">
                <div className="flex items-center space-x-2">
                  <Mail className="h-3 w-3" />
                  <span>support@terrascale.tech</span>
                </div>
                <div className="flex items-center space-x-2">
                  <MessageSquare className="h-3 w-3" />
                  <a href="https://discord.gg/terrascale" target="_blank" rel="noopener noreferrer" className="hover:text-[#F8FAFC] transition-colors">Discord Community</a>
                </div>
              </div>

              <div className="flex space-x-3">
                {socialLinks.map((social) => (
                  <a
                    key={social.name}
                    href={social.href}
                    className="p-2 bg-[#080810] border border-[#1A1A24] hover:border-[#2563EB] ts-rounded-sm transition-all duration-200 hover-lift hover-lift-active"
                    aria-label={social.name}
                    target="_blank"
                    rel="noopener noreferrer"
                  >
                    <social.icon className="h-4 w-4 text-[#94A3B8] hover:text-[#2563EB]" />
                  </a>
                ))}
              </div>
            </div>

            {Object.entries(footerLinks).map(([category, links]) => (
              <div key={category}>
                <h3 className="font-semibold text-[#F8FAFC] mb-3 text-sm">
                  <span className="terminal-prefix">///</span>
                  {category}
                </h3>
                <ul className="space-y-2">
                  {links.map((link) => (
                    <li key={link.name}>
                      {link.href.startsWith('http') ? (
                        <a
                          href={link.href}
                          target="_blank"
                          rel="noopener noreferrer"
                          className="text-[#94A3B8] hover:text-[#F8FAFC] transition-colors duration-200 text-sm"
                        >
                          {link.name}
                        </a>
                      ) : (
                        <Link
                          to={link.href}
                          className="text-[#94A3B8] hover:text-[#F8FAFC] transition-colors duration-200 text-sm"
                        >
                          {link.name}
                        </Link>
                      )}
                    </li>
                  ))}
                </ul>
              </div>
            ))}
          </div>
        </div>

        <div className="border-t border-[#1A1A24] py-4">
          <div className="flex flex-col md:flex-row justify-between items-center space-y-2 md:space-y-0">
            <p className="text-[#475569] text-sm">
              © {new Date().getFullYear()} TerraScale. All rights reserved.
            </p>
            <div className="flex items-center space-x-4 text-sm text-[#475569]">
              <div className="flex items-center space-x-1 ts-badge ts-badge-secondary ts-rounded-sm">
                <div className="w-2 h-2 bg-[#10B981] ts-rounded-sm animate-pulse"></div>
                <Zap className="h-3 w-3 text-[#10B981]" />
                <span className="text-[#10B981]">99.99% Uptime</span>
              </div>
              <div className="flex items-center space-x-1 ts-badge ts-badge-accent ts-rounded-sm">
                <ShieldCheck className="h-3 w-3 text-[#0891B2]" />
                <span className="text-[#0891B2]">SOC 2 Compliant</span>
              </div>
            </div>
          </div>
        </div>
      </div>
    </footer>
  )
}

export default Footer
