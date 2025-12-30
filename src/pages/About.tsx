import { Users, Target, Shield, Award, BookOpen, MessageSquare, Mail } from 'lucide-react'
import { Link } from 'react-router-dom'

const About = () => {
  const values = [
    {
      icon: Target,
      title: 'Transparency',
      description: 'We believe in open communication and honest relationships with our users.',
    },
    {
      icon: Users,
      title: 'Customer-Centricity',
      description: 'Every decision we make is guided by what\'s best for our customers.',
    },
    {
      icon: Shield,
      title: 'Innovation',
      description: 'We continuously push boundaries to deliver cutting-edge solutions.',
    },
    {
      icon: Award,
      title: 'Continuous Improvement',
      description: 'We never stop learning, growing, and improving our platform.',
    },
  ]

  return (
    <>
      {/* Hero Section */}
      <section className="py-20 bg-gradient-to-br from-background via-accent/10 to-background">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="text-center max-w-4xl mx-auto">
            <h1 className="text-4xl md:text-6xl font-bold text-foreground mb-6 animate-fade-in">
              About <span className="bg-gradient-to-r from-blue-600 to-purple-600 bg-clip-text text-transparent">TerraScale</span>
            </h1>
            <p className="text-xl md:text-2xl text-muted-foreground mb-8 animate-slide-up">
              Our mission is to empower businesses with scalable, secure, and innovative 
              database solutions. We believe in transparency, customer-centricity, 
              and continuous improvement.
            </p>
          </div>
        </div>
      </section>

      {/* Mission & Values Section */}
      <section className="py-20">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-16 items-center mb-20">
            <div>
              <h2 className="text-3xl md:text-4xl font-bold text-foreground mb-6">
                Our Mission
              </h2>
              <p className="text-lg text-muted-foreground mb-6">
                At TerraScale, we're on a mission to democratize enterprise-grade database 
                infrastructure. We believe that every business, regardless of size, should 
                have access to powerful, scalable, and secure database solutions.
              </p>
              <p className="text-lg text-muted-foreground mb-6">
                Founded on the principles of innovation and reliability, we've built a 
                platform that grows with your business, ensuring you're never held back 
                by technical limitations or database performance issues.
              </p>
              <div className="bg-gradient-to-r from-blue-600/10 to-purple-600/10 border-l-4 border-primary p-6 rounded-r-xl">
                <p className="text-foreground font-medium italic">
                  "Database technology should empower, not complicate. That's the driving force 
                  behind everything we build at TerraScale."
                </p>
              </div>
            </div>
            <div className="relative">
              <div className="bg-gradient-to-r from-blue-600 to-purple-600 rounded-2xl p-8 shadow-2xl">
                <div className="bg-card rounded-lg p-6 h-64 flex items-center justify-center">
                  <div className="text-center">
                    <div className="w-16 h-16 bg-gradient-to-r from-blue-600 to-purple-600 rounded-full mx-auto mb-4 flex items-center justify-center">
                      <Target className="h-8 w-8 text-white" />
                    </div>
                    <p className="text-muted-foreground font-medium">Our Vision</p>
                  </div>
                </div>
              </div>
            </div>
          </div>

          {/* Core Values */}
          <div className="text-center mb-16">
            <h2 className="text-3xl md:text-4xl font-bold text-foreground mb-4">
              Our Core Values
            </h2>
            <p className="text-xl text-muted-foreground max-w-2xl mx-auto">
              The principles that guide every decision we make and every solution we build.
            </p>
          </div>
          
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-8">
            {values.map((value, index) => (
              <div
                key={value.title}
                className="text-center p-6 bg-card border border-border rounded-xl hover:shadow-lg transition-all duration-300 group transform hover:-translate-y-1"
                style={{ animationDelay: `${index * 0.1}s` }}
              >
                <div className="w-12 h-12 bg-gradient-to-r from-blue-600/10 to-purple-600/10 rounded-lg flex items-center justify-center mx-auto mb-4 group-hover:scale-110 transition-transform duration-300">
                  <value.icon className="h-6 w-6 text-primary" />
                </div>
                <h3 className="text-xl font-semibold text-foreground mb-3">
                  {value.title}
                </h3>
                <p className="text-muted-foreground">
                  {value.description}
                </p>
              </div>
            ))}
          </div>
        </div>
      </section>

      {/* Team Section */}
      <section className="py-20 bg-gradient-to-b from-background to-accent/5">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="text-center mb-16">
            <h2 className="text-3xl md:text-4xl font-bold text-foreground mb-4">
              Our Team
            </h2>
            <p className="text-xl text-muted-foreground max-w-2xl mx-auto">
              Meet the passionate individuals building the future of scalable database technology.
            </p>
          </div>
          
          <div className="flex justify-center">
            <div className="bg-card border border-border p-8 rounded-2xl hover:shadow-xl transition-all duration-300 max-w-md transform hover:-translate-y-2">
              <div className="text-center">
                <div className="w-24 h-24 bg-gradient-to-r from-blue-600 to-purple-600 rounded-full mx-auto mb-6 flex items-center justify-center text-white text-2xl font-bold">
                  MG
                </div>
                <h3 className="text-2xl font-bold text-foreground mb-2">
                  Mario Gabriell Karaziaki Belchior
                </h3>
                <p className="text-primary font-semibold mb-4">CEO & Founder</p>
                <p className="text-muted-foreground leading-relaxed mb-4">
                  Building the future of scalable database technology with passion and vision.
                  Mario combines deep technical expertise with strategic leadership to
                  drive TerraScale's mission forward.
                </p>
                <a
                  href="mailto:mariogk@terrascale.tech"
                  className="inline-flex items-center text-sm text-muted-foreground hover:text-primary transition-colors duration-200"
                >
                  <Mail className="h-4 w-4 mr-2" />
                  mariogk@terrascale.tech
                </a>
              </div>
            </div>
          </div>
          
          {/* Join Team CTA */}
          <div className="mt-16 text-center">
            <div className="bg-gradient-to-r from-blue-600/10 to-purple-600/10 rounded-xl p-8 border border-border">
              <h3 className="text-2xl font-bold text-foreground mb-4">Join Our Team</h3>
              <p className="text-muted-foreground mb-6 max-w-2xl mx-auto">
                We're always looking for talented individuals who share our passion for building 
                innovative database solutions. Join us in shaping the future of data infrastructure.
              </p>
              <div className="flex flex-col sm:flex-row gap-4 justify-center">
                <Link
                  to="/contact"
                  className="bg-gradient-to-r from-blue-600 to-purple-600 hover:from-blue-700 hover:to-purple-700 text-white px-6 py-3 rounded-lg font-semibold transition-all duration-300 inline-flex items-center justify-center"
                >
                  <MessageSquare className="h-4 w-4 mr-2" />
                  Get in Touch
                </Link>
                <a
                  href="https://docs.terrascale.tech"
                  target="_blank"
                  rel="noopener noreferrer"
                  className="border border-border hover:bg-accent text-foreground px-6 py-3 rounded-lg font-semibold transition-all duration-300 inline-flex items-center justify-center"
                >
                  <BookOpen className="h-4 w-4 mr-2" />
                  Learn More
                </a>
              </div>
            </div>
          </div>
        </div>
      </section>

      {/* Stats Section */}
      <section className="py-20 bg-gradient-to-r from-blue-600 to-purple-600 relative overflow-hidden">
        <div className="absolute inset-0 bg-[radial-gradient(circle_at_30%_40%,rgba(255,255,255,0.1),transparent_50%)]" />
        <div className="absolute inset-0 bg-[radial-gradient(circle_at_70%_60%,rgba(255,255,255,0.05),transparent_50%)]" />
        
        <div className="relative z-10 max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="text-center mb-12">
            <h2 className="text-3xl md:text-4xl font-bold text-white mb-4">
              TerraScale by the Numbers
            </h2>
            <p className="text-xl text-blue-100">
              Trusted by developers and businesses worldwide
            </p>
          </div>
          
          <div className="grid grid-cols-1 md:grid-cols-4 gap-8 text-center text-white">
            <div className="p-6 bg-white/10 rounded-xl backdrop-blur-sm">
              <div className="text-4xl md:text-5xl font-bold mb-2">10,000+</div>
              <div className="text-blue-100">Active Developers</div>
            </div>
            <div className="p-6 bg-white/10 rounded-xl backdrop-blur-sm">
              <div className="text-4xl md:text-5xl font-bold mb-2">99.99%</div>
              <div className="text-blue-100">Uptime SLA</div>
            </div>
            <div className="p-6 bg-white/10 rounded-xl backdrop-blur-sm">
              <div className="text-4xl md:text-5xl font-bold mb-2">24/7</div>
              <div className="text-blue-100">Support</div>
            </div>
            <div className="p-6 bg-white/10 rounded-xl backdrop-blur-sm">
              <div className="text-4xl md:text-5xl font-bold mb-2">20+</div>
              <div className="text-blue-100">Global Regions</div>
            </div>
          </div>
        </div>
      </section>
    </>
  )
}

export default About