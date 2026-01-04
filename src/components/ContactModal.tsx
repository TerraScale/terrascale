import { useState } from 'react'
import { X, Calendar, Send } from 'lucide-react'

interface ContactModalProps {
  isOpen: boolean
  onClose: () => void
}

const ContactModal = ({ isOpen, onClose }: ContactModalProps) => {
  const [formData, setFormData] = useState({
    name: '',
    email: '',
    company: '',
    message: ''
  })

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault()
    // Create Google Forms submission URL with pre-filled data
    const googleFormUrl = 'https://docs.google.com/forms/d/e/1FAIpQLSdXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX/formResponse'
    const params = new URLSearchParams({
      'entry.1234567': formData.name,
      'entry.2345678': formData.email,
      'entry.3456789': formData.company,
      'entry.4567890': formData.message
    })

    // Open Google Form in new tab (would need actual form URL)
    window.open(`${googleFormUrl}?${params.toString()}`, '_blank')
    onClose()
  }

  const handleCalendlyClick = () => {
    // Open Calendly in new tab
    window.open('https://calendly.com/terrascale/enterprise-consultation', '_blank')
  }

  if (!isOpen) return null

  return (
    <div className="fixed inset-0 bg-black/50 backdrop-blur-sm flex items-center justify-center z-50 p-4">
      <div className="bg-[#080810] border border-[#1A1A24] ts-rounded-xl max-w-md w-full max-h-[90vh] overflow-y-auto ts-glass-frosted">
        <div className="p-6">
          <div className="flex items-center justify-between mb-6">
            <h2 className="text-2xl font-bold text-[#F8FAFC]">Contact Enterprise Sales</h2>
            <button
              onClick={onClose}
              className="p-2 hover:bg-[#0C0C10] ts-rounded-lg transition-colors text-[#94A3B8] hover:text-[#F8FAFC]"
            >
              <X className="h-5 w-5" />
            </button>
          </div>

          <div className="space-y-6">
            {/* Updated gradient to use theme colors */}
            <div className="text-center p-4 bg-gradient-to-r from-[#2563EB]/10 via-[#8B5CF6]/10 to-[#0891B2]/10 ts-rounded-lg border border-[#1A1A24]">
              <p className="text-sm text-[#94A3B8]">
                Get personalized pricing and dedicated support for your enterprise needs
              </p>
            </div>

            <form onSubmit={handleSubmit} className="space-y-4">
              <div>
                <label className="block text-sm font-medium text-[#F8FAFC] mb-2">
                  Full Name *
                </label>
                <input
                  type="text"
                  required
                  value={formData.name}
                  onChange={(e) => setFormData({ ...formData, name: e.target.value })}
                  className="ts-input w-full"
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-[#F8FAFC] mb-2">
                  Business Email *
                </label>
                <input
                  type="email"
                  required
                  value={formData.email}
                  onChange={(e) => setFormData({ ...formData, email: e.target.value })}
                  className="ts-input w-full"
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-[#F8FAFC] mb-2">
                  Company Name *
                </label>
                <input
                  type="text"
                  required
                  value={formData.company}
                  onChange={(e) => setFormData({ ...formData, company: e.target.value })}
                  className="ts-input w-full"
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-[#F8FAFC] mb-2">
                  Tell us about your needs
                </label>
                <textarea
                  rows={3}
                  value={formData.message}
                  onChange={(e) => setFormData({ ...formData, message: e.target.value })}
                  placeholder="Describe your project requirements, expected scale, and any specific needs..."
                  className="ts-input w-full resize-none"
                />
              </div>

              <div className="flex flex-col sm:flex-row gap-3">
                {/* Updated gradient button to use theme rainbow gradient */}
                <button
                  type="submit"
                  className="flex-1 ts-btn-primary ts-rounded-md font-medium flex items-center justify-center gap-2"
                >
                  <Send className="h-4 w-4" />
                  Send Message
                </button>
                <button
                  type="button"
                  onClick={handleCalendlyClick}
                  className="flex-1 ts-btn-glass ts-rounded-md font-medium flex items-center justify-center gap-2"
                >
                  <Calendar className="h-4 w-4" />
                  Schedule Call
                </button>
              </div>
            </form>

            <div className="text-center text-sm text-[#94A3B8]">
              <p>Our team typically responds within 24 hours</p>
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}

export default ContactModal
