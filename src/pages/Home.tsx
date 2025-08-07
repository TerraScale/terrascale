import { Link } from 'react-router-dom'
import { 
  ArrowRight, 
  Play, 
  Zap, 
  BarChart3, 
  Shield, 
  TrendingUp,
  Star,
  Users,
  ChevronRight,
  Database,
  Server,
  Globe,
  Code2,
  BookOpen
} from 'lucide-react'

const Home = () => {
  const features = [
    {
      icon: Zap,
      title: 'Lightning Fast',
      description: 'Blazing fast queries with global edge caching and optimized indexing.',
    },
    {
      icon: BarChart3,
      title: 'Real-Time Analytics',
      description: 'Monitor your data and performance with real-time insights and dashboards.',
    },
    {
      icon: Shield,
      title: 'Enterprise Security',
      description: 'SOC 2 compliant with end-to-end encryption and advanced access controls.',
    },
    {
      icon: Globe,
      title: 'Global Scale',
      description: 'Deploy worldwide with automatic failover and geographic distribution.',
    },
  ]

  const testimonials = [
    {
      name: 'Sam Carter',
      role: 'CTO, ExampleCorp',
      content: 'TerraScale transformed our data infrastructure. The performance gains are incredible.',
      avatar: 'SC',
    },
    {
      name: 'Priya Patel',
      role: 'Founder, Startly',
      content: 'The developer experience is outstanding. We shipped faster than ever before.',
      avatar: 'PP',
    },
    {
      name: 'Liam Nguyen',
      role: 'Product Manager',
      content: 'Reliable, secure, and scales effortlessly. TerraScale is our backbone.',
      avatar: 'LN',
    },
  ]

  const stats = [
    { value: '99.99%', label: 'Uptime SLA' },
    { value: '<10ms', label: 'Query Latency' },
    { value: '1M+', label: 'Queries/Second' },
    { value: '50+', label: 'Global Regions' },
  ]

  return (
    <>
      {/* Hero Section */}
      <section className="relative min-h-screen flex items-center justify-center overflow-hidden">
        {/* Animated Background */}
        <div className="absolute inset-0 bg-gradient-to-br from-background via-background to-accent/20">
          <div className="absolute inset-0 bg-[radial-gradient(circle_at_30%_40%,rgba(67,121,242,0.1),transparent_50%)]" />
          <div className="absolute inset-0 bg-[radial-gradient(circle_at_70%_60%,rgba(110,194,7,0.1),transparent_50%)]" />
          <div className="absolute inset-0 bg-[radial-gradient(circle_at_50%_50%,rgba(67,121,242,0.05),transparent_50%)]" />
        </div>
        
        {/* Floating Elements - Earth theme */}
        <div className="absolute top-20 left-10 w-4 h-4 bg-[#4379F2] rounded-full animate-bounce animate-pulse-glow" style={{ animationDelay: '0s' }} />
        <div className="absolute top-40 right-20 w-3 h-3 bg-[#6EC207] rounded-full animate-bounce" style={{ animationDelay: '0.5s' }} />
        <div className="absolute bottom-40 left-20 w-5 h-5 bg-[#4379F2]/70 rounded-full animate-float" style={{ animationDelay: '1s' }} />
        <div className="absolute bottom-20 right-10 w-2 h-2 bg-[#6EC207]/70 rounded-full animate-bounce" style={{ animationDelay: '1.5s' }} />
        <div className="absolute top-1/2 left-1/4 w-3 h-3 bg-[#4379F2]/50 rounded-full animate-pulse" />
        <div className="absolute top-1/3 right-1/3 w-4 h-4 bg-[#6EC207]/50 rounded-full animate-float" style={{ animationDelay: '2s' }} />
        
        {/* Additional floating decorations */}
        <div className="absolute top-1/4 left-1/2 w-6 h-6 border-2 border-[#4379F2]/30 rounded-full animate-float" style={{ animationDelay: '3s' }} />
        <div className="absolute bottom-1/3 right-1/4 w-8 h-8 border border-[#6EC207]/20 rounded-full animate-pulse" style={{ animationDelay: '4s' }} />
        
        <div className="relative z-10 max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-20">
          <div className="text-center max-w-4xl mx-auto">
            {/* Badge */}
            <div className="inline-flex items-center px-4 py-2 bg-gradient-to-r from-[#4379F2]/10 to-[#6EC207]/10 border border-border rounded-full text-sm font-medium text-foreground mb-8 animate-fade-in">
              <Database className="h-4 w-4 mr-2 text-primary" />
              Database as a Service Platform
            </div>
            
            {/* Main Heading */}
            <h1 className="text-5xl md:text-7xl lg:text-8xl font-bold text-foreground mb-8 animate-fade-in">
              Scale Your{' '}
              <span className="bg-gradient-to-r from-[#4379F2] via-[#6EC207] to-[#4379F2] bg-clip-text text-transparent animate-gradient bg-[length:200%_auto]">
                Database
              </span>
              <br />
              Effortlessly
            </h1>
            
            {/* Subtitle */}
            <p className="text-xl md:text-2xl text-muted-foreground mb-12 max-w-3xl mx-auto animate-slide-up">
              The most advanced database platform for modern applications. Deploy, scale, and manage your data with enterprise-grade security and performance.
            </p>
            
            {/* CTA Buttons */}
            <div className="flex flex-col sm:flex-row gap-4 justify-center items-center mb-16 animate-slide-up">
              <Link
                to="/contact"
                className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] hover:from-[#3366E6] hover:to-[#5CAE06] text-white px-8 py-4 rounded-xl text-lg font-semibold transition-all duration-300 transform hover:scale-105 inline-flex items-center shadow-lg hover:shadow-xl"
              >
                Start Building Free
                <ArrowRight className="ml-2 h-5 w-5" />
              </Link>
              <Link
                to="https://docs.terrascale.tech"
                className="border border-border hover:bg-accent text-foreground px-8 py-4 rounded-xl text-lg font-semibold transition-all duration-300 inline-flex items-center"
              >
                <BookOpen className="mr-2 h-5 w-5" />
                View Documentation
              </Link>
            </div>
            
            {/* Stats */}
            <div className="grid grid-cols-2 md:grid-cols-4 gap-8 animate-fade-in">
              {stats.map((stat, index) => (
                <div key={index} className="text-center">
                  <div className="text-2xl md:text-3xl font-bold text-foreground">{stat.value}</div>
                  <div className="text-sm text-muted-foreground">{stat.label}</div>
                </div>
              ))}
            </div>
          </div>
        </div>
        
        {/* Scroll Indicator */}
        <div className="absolute bottom-8 left-1/2 transform -translate-x-1/2 animate-bounce">
          <div className="w-6 h-10 border-2 border-border rounded-full flex justify-center">
            <div className="w-1 h-3 bg-foreground rounded-full mt-2 animate-pulse" />
          </div>
        </div>
      </section>

      {/* Features Grid */}
      <section className="py-20 bg-gradient-to-b from-background to-accent/10">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="text-center mb-16">
            <h2 className="text-3xl md:text-5xl font-bold text-foreground mb-6">
              Why Choose <span className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] bg-clip-text text-transparent">TerraScale</span>
            </h2>
            <p className="text-xl text-muted-foreground max-w-3xl mx-auto">
              Built for developers, trusted by enterprises. Experience the next generation of database infrastructure.
            </p>
          </div>
          
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-8">
            {features.map((feature, index) => (
              <div
                key={feature.title}
                className="bg-card border border-border p-8 rounded-2xl hover:shadow-lg transition-all duration-300 group transform hover:-translate-y-2"
                style={{ animationDelay: `${index * 0.1}s` }}
              >
                <div className="w-14 h-14 bg-gradient-to-r from-[#4379F2]/10 to-[#6EC207]/10 rounded-xl flex items-center justify-center mb-6 group-hover:scale-110 transition-transform duration-300">
                  <feature.icon className="h-7 w-7 text-primary" />
                </div>
                <h3 className="text-xl font-bold text-foreground mb-3">
                  {feature.title}
                </h3>
                <p className="text-muted-foreground leading-relaxed">
                  {feature.description}
                </p>
              </div>
            ))}
          </div>
        </div>
      </section>

      {/* Code Example Section */}
      <section className="py-20">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-16 items-center">
            <div>
              <h2 className="text-3xl md:text-4xl font-bold text-foreground mb-6">
                Get started in <span className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] bg-clip-text text-transparent">seconds</span>
              </h2>
              <p className="text-xl text-muted-foreground mb-8">
                Simple APIs, powerful features. Deploy your database with a single command and start building immediately.
              </p>
              <div className="flex items-center space-x-4">
                <Link
                  to="/contact"
                  className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] hover:from-[#3366E6] hover:to-[#5CAE06] text-white px-6 py-3 rounded-lg font-semibold transition-all duration-300 inline-flex items-center"
                >
                  Start Free Trial
                  <ArrowRight className="ml-2 h-4 w-4" />
                </Link>
                <Link
                  to="/pricing"
                  className="text-primary hover:text-primary/80 font-semibold inline-flex items-center"
                >
                  View Pricing
                  <ChevronRight className="ml-1 h-4 w-4" />
                </Link>
              </div>
            </div>
            
            <div className="bg-card border border-border rounded-2xl p-6 overflow-hidden">
              <div className="flex items-center space-x-2 mb-4">
                <div className="w-3 h-3 bg-red-500 rounded-full" />
                <div className="w-3 h-3 bg-yellow-500 rounded-full" />
                <div className="w-3 h-3 bg-green-500 rounded-full" />
                <Code2 className="h-4 w-4 ml-auto text-muted-foreground" />
              </div>
              <pre className="text-sm text-foreground overflow-x-auto">
                <code>{`import { TerraScale } from '@terrascale/sdk'

const db = new TerraScale({
  apiKey: process.env.TERRASCALE_API_KEY,
  region: 'us-west-2'
})

// Create a new document
await db.collection('users').create({
  name: 'John Doe',
  email: 'john@example.com'
})

// Query with real-time filters
const users = await db.collection('users')
  .where('status', '==', 'active')
  .orderBy('created_at')
  .limit(10)
  .get()`}</code>
              </pre>
            </div>
          </div>
        </div>
      </section>

      {/* Testimonials Section */}
      <section className="py-20 bg-gradient-to-r from-accent/5 to-accent/10">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="text-center mb-16">
            <h2 className="text-3xl md:text-4xl font-bold text-foreground mb-4">
              Trusted by developers worldwide
            </h2>
            <div className="flex items-center justify-center space-x-2 text-yellow-500 mb-2">
              {[...Array(5)].map((_, i) => (
                <Star key={i} className="h-5 w-5 fill-current" />
              ))}
            </div>
            <p className="text-muted-foreground">Loved by 10,000+ developers and growing</p>
          </div>
          
          <div className="grid grid-cols-1 md:grid-cols-3 gap-8">
            {testimonials.map((testimonial, index) => (
              <div
                key={testimonial.name}
                className="bg-card border border-border p-8 rounded-2xl hover:shadow-lg transition-all duration-300 transform hover:-translate-y-1"
                style={{ animationDelay: `${index * 0.1}s` }}
              >
                <div className="flex items-center space-x-4 mb-6">
                  <div className="w-12 h-12 bg-gradient-to-r from-blue-600 to-purple-600 rounded-full flex items-center justify-center text-white font-bold">
                    {testimonial.avatar}
                  </div>
                  <div>
                    <h4 className="font-bold text-foreground">{testimonial.name}</h4>
                    <p className="text-muted-foreground text-sm">{testimonial.role}</p>
                  </div>
                </div>
                <p className="text-foreground italic leading-relaxed">"{testimonial.content}"</p>
              </div>
            ))}
          </div>
        </div>
      </section>

      {/* Bottom CTA Section */}
      <section className="py-20 bg-gradient-to-r from-[#4379F2] to-[#6EC207] relative overflow-hidden">
        <div className="absolute inset-0 bg-[radial-gradient(circle_at_30%_40%,rgba(255,255,255,0.1),transparent_50%)]" />
        <div className="absolute inset-0 bg-[radial-gradient(circle_at_70%_60%,rgba(255,255,255,0.05),transparent_50%)]" />
        
        <div className="relative z-10 max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 text-center">
          <h2 className="text-3xl md:text-5xl font-bold text-white mb-6">
            Ready to build the future?
          </h2>
          <p className="text-xl text-white/80 mb-10 max-w-2xl mx-auto">
            Join thousands of developers already using TerraScale to build scalable, reliable applications.
          </p>
          <div className="flex flex-col sm:flex-row gap-4 justify-center items-center">
            <Link
              to="/contact"
              className="bg-white text-[#4379F2] hover:bg-gray-50 font-bold py-4 px-8 rounded-xl transition-all duration-300 transform hover:scale-105 inline-flex items-center shadow-lg"
            >
              Start Building Today
              <ArrowRight className="ml-2 h-5 w-5" />
            </Link>
            <Link
              to="/pricing"
              className="border-2 border-white text-white hover:bg-white hover:text-[#4379F2] font-bold py-4 px-8 rounded-xl transition-all duration-300"
            >
              Explore Pricing
            </Link>
          </div>
        </div>
      </section>
    </>
  )
}

export default Home