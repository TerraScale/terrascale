import { Link } from 'react-router-dom'
import { Twitter, Linkedin, Github, Mail, Phone, MapPin, MessageSquare } from 'lucide-react'

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
      { name: 'Documentation', href: 'https://docs.terrascale.com' },
      { name: 'API Reference', href: 'https://docs.terrascale.com/api' },
    ],
    Company: [
      { name: 'About', href: '/about' },
      { name: 'Contact', href: '/contact' },
      { name: 'Status', href: 'https://status.terrascale.com' },
      { name: 'Blog', href: 'https://blog.terrascale.com' },
    ],
    Resources: [
      { name: 'Help Center', href: 'https://help.terrascale.com' },
      { name: 'Community', href: 'https://discord.gg/terrascale' },
      { name: 'Security', href: '/security' },
      { name: 'Terms', href: '/terms' },
    ],
  }

  return (
    <footer className="bg-card border-t border-border">
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
        {/* Main Footer Content */}
        <div className="py-8">
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-5 gap-8">
            {/* Brand Column */}
            <div className="lg:col-span-2">
              <Link to="/" className="flex items-center space-x-2 mb-4">
                <div className="w-8 h-8 bg-gradient-to-r from-blue-600 to-purple-600 rounded-lg flex items-center justify-center">
                  <span className="text-white font-bold text-sm">T</span>
                </div>
                <span className="text-xl font-bold text-foreground">TerraScale</span>
              </Link>
              <p className="text-muted-foreground mb-4 max-w-sm text-sm">
                Empowering businesses with secure, scalable, and innovative cloud solutions. 
                Scale your tech, effortlessly.
              </p>
              
              {/* Contact Info */}
              <div className="space-y-2 text-sm text-muted-foreground mb-4">
                <div className="flex items-center space-x-2">
                  <Mail className="h-3 w-3" />
                  <span>contact@terrascale.com</span>
                </div>
                <div className="flex items-center space-x-2">
                  <Phone className="h-3 w-3" />
                  <span>+1 (555) 123-4567</span>
                </div>
              </div>

              {/* Social Links */}
              <div className="flex space-x-3">
                {socialLinks.map((social) => (
                  <a
                    key={social.name}
                    href={social.href}
                    className="p-2 bg-accent hover:bg-accent/80 rounded-lg transition-colors duration-200"
                    aria-label={social.name}
                    target="_blank"
                    rel="noopener noreferrer"
                  >
                    <social.icon className="h-4 w-4 text-muted-foreground" />
                  </a>
                ))}
              </div>
            </div>

            {/* Links Columns */}
            {Object.entries(footerLinks).map(([category, links]) => (
              <div key={category}>
                <h3 className="font-semibold text-foreground mb-3 text-sm">{category}</h3>
                <ul className="space-y-2">
                  {links.map((link) => (
                    <li key={link.name}>
                      {link.href.startsWith('http') ? (
                        <a
                          href={link.href}
                          target="_blank"
                          rel="noopener noreferrer"
                          className="text-muted-foreground hover:text-foreground transition-colors duration-200 text-sm"
                        >
                          {link.name}
                        </a>
                      ) : (
                        <Link
                          to={link.href}
                          className="text-muted-foreground hover:text-foreground transition-colors duration-200 text-sm"
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

        {/* Bottom Bar */}
        <div className="border-t border-border py-4">
          <div className="flex flex-col md:flex-row justify-between items-center space-y-2 md:space-y-0">
            <p className="text-muted-foreground text-sm">
              © {new Date().getFullYear()} TerraScale. All rights reserved.
            </p>
            <div className="flex items-center space-x-4 text-sm text-muted-foreground">
              <div className="flex items-center space-x-1">
                <div className="w-2 h-2 bg-green-500 rounded-full animate-pulse"></div>
                <span>99.99% Uptime</span>
              </div>
              <span>SOC 2 Compliant</span>
            </div>
          </div>
        </div>
      </div>
    </footer>
  )
}

export default Footer