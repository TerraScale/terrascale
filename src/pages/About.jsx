import { Users, Target, Shield, Award } from 'lucide-react'

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
      <section className="section-padding bg-gradient-to-b from-gray-50 to-white">
        <div className="max-w-7xl mx-auto container-padding">
          <div className="text-center max-w-4xl mx-auto">
            <h1 className="text-4xl md:text-6xl font-bold text-gray-900 mb-6">
              About <span className="gradient-text">TerraScale</span>
            </h1>
            <p className="text-xl md:text-2xl text-gray-600 mb-8">
              Our mission is to empower businesses with scalable, secure, and innovative 
              technology solutions. We believe in transparency, customer-centricity, 
              and continuous improvement.
            </p>
          </div>
        </div>
      </section>

      {/* Mission & Values Section */}
      <section className="section-padding">
        <div className="max-w-7xl mx-auto container-padding">
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-16 items-center mb-20">
            <div>
              <h2 className="text-3xl md:text-4xl font-bold text-gray-900 mb-6">
                Our Mission
              </h2>
              <p className="text-lg text-gray-600 mb-6">
                At TerraScale, we're on a mission to democratize enterprise-grade cloud 
                infrastructure. We believe that every business, regardless of size, should 
                have access to powerful, scalable, and secure technology solutions.
              </p>
              <p className="text-lg text-gray-600 mb-6">
                Founded on the principles of innovation and reliability, we've built a 
                platform that grows with your business, ensuring you're never held back 
                by technical limitations.
              </p>
              <div className="bg-primary-50 border-l-4 border-primary-500 p-6 rounded-r-lg">
                <p className="text-primary-800 font-medium italic">
                  "Technology should empower, not complicate. That's the driving force 
                  behind everything we build at TerraScale."
                </p>
              </div>
            </div>
            <div className="relative">
              <div className="bg-gradient-to-r from-primary-500 to-primary-700 rounded-2xl p-8 shadow-2xl">
                <div className="bg-white rounded-lg p-6 h-64 flex items-center justify-center">
                  <div className="text-center">
                    <div className="w-16 h-16 bg-gradient-to-r from-primary-600 to-primary-700 rounded-full mx-auto mb-4 flex items-center justify-center">
                      <Target className="h-8 w-8 text-white" />
                    </div>
                    <p className="text-gray-600 font-medium">Our Vision</p>
                  </div>
                </div>
              </div>
            </div>
          </div>

          {/* Core Values */}
          <div className="text-center mb-16">
            <h2 className="text-3xl md:text-4xl font-bold text-gray-900 mb-4">
              Our Core Values
            </h2>
            <p className="text-xl text-gray-600 max-w-2xl mx-auto">
              The principles that guide every decision we make and every solution we build.
            </p>
          </div>
          
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-8">
            {values.map((value, index) => (
              <div
                key={value.title}
                className="text-center p-6 rounded-xl hover:shadow-lg transition-shadow duration-300 group"
                style={{ animationDelay: `${index * 0.1}s` }}
              >
                <div className="w-12 h-12 bg-primary-100 rounded-lg flex items-center justify-center mx-auto mb-4 group-hover:bg-primary-200 transition-colors duration-300">
                  <value.icon className="h-6 w-6 text-primary-600" />
                </div>
                <h3 className="text-xl font-semibold text-gray-900 mb-3">
                  {value.title}
                </h3>
                <p className="text-gray-600">
                  {value.description}
                </p>
              </div>
            ))}
          </div>
        </div>
      </section>

      {/* Team Section */}
      <section className="section-padding bg-gray-50">
        <div className="max-w-7xl mx-auto container-padding">
          <div className="text-center mb-16">
            <h2 className="text-3xl md:text-4xl font-bold text-gray-900 mb-4">
              Our Team
            </h2>
            <p className="text-xl text-gray-600 max-w-2xl mx-auto">
              Meet the passionate individuals building the future of scalable technology.
            </p>
          </div>
          
          <div className="flex justify-center">
            <div className="bg-white p-8 rounded-2xl shadow-lg hover:shadow-xl transition-shadow duration-300 max-w-md">
              <div className="text-center">
                <div className="w-24 h-24 bg-gradient-to-r from-primary-600 to-primary-700 rounded-full mx-auto mb-6 flex items-center justify-center text-white text-2xl font-bold">
                  MG
                </div>
                <h3 className="text-2xl font-bold text-gray-900 mb-2">
                  Mario Gabriell Karaziaki Belchior
                </h3>
                <p className="text-primary-600 font-semibold mb-4">CEO & Founder</p>
                <p className="text-gray-600 leading-relaxed">
                  Building the future of scalable technology with passion and vision. 
                  Mario combines deep technical expertise with strategic leadership to 
                  drive TerraScale's mission forward.
                </p>
              </div>
            </div>
          </div>
        </div>
      </section>

      {/* Stats Section */}
      <section className="section-padding bg-gradient-to-r from-primary-600 to-primary-700">
        <div className="max-w-7xl mx-auto container-padding">
          <div className="grid grid-cols-1 md:grid-cols-4 gap-8 text-center text-white">
            <div>
              <div className="text-4xl md:text-5xl font-bold mb-2">1,000+</div>
              <div className="text-primary-100">Happy Users</div>
            </div>
            <div>
              <div className="text-4xl md:text-5xl font-bold mb-2">99.99%</div>
              <div className="text-primary-100">Uptime</div>
            </div>
            <div>
              <div className="text-4xl md:text-5xl font-bold mb-2">24/7</div>
              <div className="text-primary-100">Support</div>
            </div>
            <div>
              <div className="text-4xl md:text-5xl font-bold mb-2">50+</div>
              <div className="text-primary-100">Countries</div>
            </div>
          </div>
        </div>
      </section>
    </>
  )
}

export default About